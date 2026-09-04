// Serviço isolado de conversão DOCX -> PDF.
//
// Existe porque a imagem oficial `syncfusion/word-processor-server` NÃO converte
// para PDF: ela não embarca o `DocIORenderer`. O `Export` dela aceita
// `format: "Pdf"`, cai no caso padrão do switch e devolve um `.doc` legado com
// `Content-Type: application/msword` — confirmado contra o servidor de produção.
//
// A decisão de projeto (ver DEPLOY.md > "Serviço de PDF") é NÃO substituir aquela
// imagem, e sim rodar este serviço ao lado dela. Assim as rotas de hoje — `Import`
// acima de todas, mas também `Export`, `ExportSFDT`, `MailMerge`, `SpellCheck`… —
// continuam sendo servidas pelo binário oficial, byte por byte como hoje.
//
// Este processo expõe exatamente DUAS rotas:
//   GET  /health/live                       — liveness, não toca no Syncfusion
//   POST /api/documenteditor/ConvertToPdf   — .docx entra, application/pdf sai

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Syncfusion.DocIORenderer;
using Syncfusion.Licensing;
using Syncfusion.Pdf;

// Aliases explícitos, como no controller oficial: vários namespaces do Syncfusion
// declaram tipos de mesmo nome, e o `using` solto deixa a compilação na sorte.
using WDocument = Syncfusion.DocIO.DLS.WordDocument;
using WFormatType = Syncfusion.DocIO.FormatType;

// Mesmo teto do Caddy (`request_body max_size 30MB`). Ter os dois evita que um
// upload gigante chegue a alocar memória aqui caso alguém fale com o serviço
// direto pela rede interna, sem passar pelo proxy.
const long MaxUploadBytes = 30L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = MaxUploadBytes);

var app = builder.Build();

// A licença é a MESMA variável que o word-processor-server já usa, de propósito:
// o compose passa `SYNCFUSION_LICENSE_KEY` para os dois containers.
//
// A partir da 31.x a Syncfusion SEPAROU as edições: a chave que habilita o DOCX
// Editor (`WordEditor`) pode não habilitar o Document SDK (`Word`, `WordToPDF`) —
// e é `WordToPDF` que este serviço precisa. Por isso não basta registrar: é
// preciso VALIDAR e dizer o que a chave cobre.
//
// `RegisterLicense` aceita VÁRIAS chaves numa chamada só, separadas por `;` ou `,`
// (documentado pela Syncfusion). Então uma combinação legítima — por exemplo DOCX
// Editor + Document SDK — cabe inteira na variável de ambiente, sem mudar código.
var licenca = Environment.GetEnvironmentVariable("SYNCFUSION_LICENSE_KEY");
var chavePresente = !string.IsNullOrWhiteSpace(licenca);

// Só a CONTAGEM, nunca o conteúdo. A chave não pode aparecer em log nem em resposta.
var quantidadeDeChaves = chavePresente
    ? licenca!.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length
    : 0;

if (chavePresente)
{
    // `!`: o compilador não liga `chavePresente` a `licenca` sozinho.
    SyncfusionLicenseProvider.RegisterLicense(licenca!);
}

var versaoDocIORenderer = typeof(DocIORenderer).Assembly.GetName().Version?.ToString() ?? "desconhecida";

// As três que decidem se este serviço pode existir sem marca d'água.
var focos = new[] { "WordToPDF", "Word", "WordEditor" }
    .ToDictionary(nome => nome, nome => ValidarPlataforma(nome, licenca));

// Mapa completo: é ele que diz QUAL edição comprar, se faltar alguma.
var cobertura = new SortedDictionary<string, bool>(StringComparer.Ordinal);
foreach (var plataforma in Enum.GetValues<Platform>())
{
    try
    {
        cobertura[plataforma.ToString()] = SyncfusionLicenseProvider.ValidateLicense(plataforma);
    }
    catch
    {
        // Uma plataforma que explode ao validar não pode derrubar o diagnóstico inteiro.
        cobertura[plataforma.ToString()] = false;
    }
}

app.Logger.LogInformation(
    "Licença: chave presente={presente}, nº de chaves={n}, DocIORenderer={versao}",
    chavePresente,
    quantidadeDeChaves,
    versaoDocIORenderer);

foreach (var (nome, resultado) in focos)
{
    app.Logger.LogInformation(
        "Licença[{plataforma}]: valida={valida} existeNestaVersao={existe} — {mensagem}",
        nome,
        resultado.Valida,
        resultado.ExisteNestaVersao,
        resultado.Mensagem);
}

app.Logger.LogInformation(
    "Licença: plataformas cobertas = {cobertas}",
    string.Join(", ", cobertura.Where(par => par.Value).Select(par => par.Key)) is { Length: > 0 } lista
        ? lista
        : "NENHUMA");

if (!chavePresente)
{
    app.Logger.LogWarning("SYNCFUSION_LICENSE_KEY vazia: o PDF sai com marca d'água de avaliação.");
}
else if (!focos["WordToPDF"].Valida)
{
    app.Logger.LogError(
        "A chave NÃO cobre WordToPDF: o PDF sai com marca d'água. Não há contorno no código — " +
        "é preciso a licença do Document SDK (Word/WordToPDF) para a versão {versao}.",
        versaoDocIORenderer);
}

// Diagnóstico legível por HTTP: só booleanos, nunca a chave. Serve para conferir de
// fora, sem acesso ao host — que foi exatamente o que faltou no primeiro deploy.
app.MapGet("/api/documenteditor/LicenseStatus", () => Results.Json(new
{
    chavePresente,
    quantidadeDeChaves,
    versaoDocIORenderer,
    focos = focos.ToDictionary(
        par => par.Key,
        par => new { par.Value.Valida, par.Value.ExisteNestaVersao, par.Value.Mensagem }),
    plataformasCobertas = cobertura.Where(par => par.Value).Select(par => par.Key).ToArray(),
    plataformasDescobertas = cobertura.Where(par => !par.Value).Select(par => par.Key).ToArray(),
}));

app.MapGet("/health/live", () => Results.Json(new
{
    status = "ok",
    service = "pdf-service",
    mode = "live",
}));

app.MapPost("/api/documenteditor/ConvertToPdf", async (HttpRequest req) =>
{
    if (!ChaveConfere(req))
    {
        return Results.Json(new
        {
            error = "unauthorized",
            hint = "PDF_API_KEY está definida no servidor; mande o header X-Api-Key.",
        }, statusCode: StatusCodes.Status401Unauthorized);
    }

    // Duas formas de mandar o arquivo:
    //   1) multipart, campo `files` — igual ao `Import`, para reaproveitar quem já sabe falar com ele;
    //   2) corpo binário cru + `?fileName=` — mais simples de montar numa Edge Function.
    var nomeArquivo = "documento.docx";
    Stream entrada;

    if (req.HasFormContentType)
    {
        var form = await req.ReadFormAsync();
        if (form.Files.Count == 0)
        {
            return Results.Json(new
            {
                error = "nenhum arquivo enviado",
                hint = "multipart com o campo `files`, ou o .docx cru no corpo com ?fileName=",
            }, statusCode: StatusCodes.Status400BadRequest);
        }

        var arquivo = form.Files[0];
        if (!string.IsNullOrWhiteSpace(arquivo.FileName))
        {
            nomeArquivo = arquivo.FileName;
        }

        var buffer = new MemoryStream();
        await arquivo.CopyToAsync(buffer);
        buffer.Position = 0;
        entrada = buffer;
    }
    else
    {
        var buffer = new MemoryStream();
        await req.Body.CopyToAsync(buffer);
        if (buffer.Length == 0)
        {
            buffer.Dispose();
            return Results.Json(new
            {
                error = "corpo vazio",
                hint = "multipart com o campo `files`, ou o .docx cru no corpo com ?fileName=",
            }, statusCode: StatusCodes.Status400BadRequest);
        }

        buffer.Position = 0;
        entrada = buffer;

        if (req.Query.TryGetValue("fileName", out var nomeNaQuery) && !string.IsNullOrWhiteSpace(nomeNaQuery))
        {
            nomeArquivo = nomeNaQuery.ToString();
        }
    }

    WDocument? documento = null;
    DocIORenderer? renderizador = null;
    PdfDocument? pdf = null;

    try
    {
        documento = new WDocument(entrada, FormatoPeloNome(nomeArquivo));
        renderizador = new DocIORenderer();
        pdf = renderizador.ConvertToPDF(documento);

        var saida = new MemoryStream();
        pdf.Save(saida);
        saida.Position = 0;

        var nomeDeSaida = Path.ChangeExtension(Path.GetFileName(nomeArquivo), ".pdf");
        app.Logger.LogInformation(
            "Convertido {arquivo} -> PDF ({bytes} bytes).",
            nomeArquivo,
            saida.Length);

        return Results.File(saida, "application/pdf", nomeDeSaida);
    }
    catch (Exception erro)
    {
        app.Logger.LogError(erro, "Falha ao converter {arquivo}.", nomeArquivo);
        // 422 e não 500: o pedido chegou inteiro e foi entendido; o que não deu
        // foi transformar ESTE documento. Quem chama consegue distinguir isso de
        // "o serviço caiu" sem ler log.
        return Results.Json(new
        {
            error = "conversao falhou",
            detail = erro.Message,
        }, statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    finally
    {
        pdf?.Close(true);
        renderizador?.Dispose();
        documento?.Close();
        entrada.Dispose();
    }
});

app.Run();

// Valida UMA plataforma pelo NOME, não pelo membro do enum. É de propósito: os
// membros do `Platform` mudaram entre versões (a 34.1.29 removeu WPF, Blazor e
// companhia), e um nome que não existe mais viraria erro de compilação em vez de
// diagnóstico. Assim, "não existe nesta versão" é uma resposta, não uma quebra.
static ResultadoDeLicenca ValidarPlataforma(string nome, string? chave)
{
    if (!Enum.TryParse<Platform>(nome, ignoreCase: false, out var plataforma))
    {
        return new ResultadoDeLicenca(false, false, $"Platform.{nome} não existe nesta versão do Syncfusion.Licensing.");
    }

    try
    {
        var valida = SyncfusionLicenseProvider.ValidateLicense(plataforma, out var mensagem);
        return new ResultadoDeLicenca(valida, true, Redigir(mensagem, chave));
    }
    catch (Exception erro)
    {
        return new ResultadoDeLicenca(false, true, Redigir(erro.Message, chave));
    }
}

// Rede de segurança: se a mensagem da Syncfusion ecoar a chave, ela não sai daqui.
static string Redigir(string? mensagem, string? chave)
{
    if (string.IsNullOrEmpty(mensagem))
    {
        return string.Empty;
    }

    if (string.IsNullOrWhiteSpace(chave))
    {
        return mensagem;
    }

    var limpa = mensagem;
    foreach (var parte in chave.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (parte.Length >= 8)
        {
            limpa = limpa.Replace(parte, "[CHAVE OMITIDA]", StringComparison.Ordinal);
        }
    }

    return limpa;
}

// Gate opcional. Vazio = desligado, que é o estado de hoje do serviço (o Caddy
// filtra Origin, o que não protege chamada servidor-a-servidor). Preenchido, vale
// para ESTA rota apenas — e aqui a chave é segredo de verdade, porque quem chama
// é uma Edge Function, não um bundle de navegador.
static bool ChaveConfere(HttpRequest req)
{
    var esperada = Environment.GetEnvironmentVariable("PDF_API_KEY");
    if (string.IsNullOrWhiteSpace(esperada))
    {
        return true;
    }

    var recebida = req.Headers["X-Api-Key"].ToString();
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(recebida),
        Encoding.UTF8.GetBytes(esperada));
}

// Mesmo mapa de extensões do controller oficial (GetWFormatType), com Docx como
// padrão em vez de exceção: quem manda corpo cru pode não ter nome de arquivo.
static WFormatType FormatoPeloNome(string nome)
{
    return Path.GetExtension(nome).ToLowerInvariant() switch
    {
        ".doc" => WFormatType.Doc,
        ".dot" => WFormatType.Dot,
        ".docm" => WFormatType.Docm,
        ".dotm" => WFormatType.Dotm,
        ".dotx" => WFormatType.Dotx,
        ".rtf" => WFormatType.Rtf,
        ".odt" => WFormatType.Odt,
        ".xml" => WFormatType.WordML,
        ".txt" => WFormatType.Txt,
        _ => WFormatType.Docx,
    };
}

// Declarado no FIM: num programa top-level, qualquer declaração de tipo precisa vir
// depois de todas as instruções — e funções locais contam como instruções (CS8803).
internal readonly record struct ResultadoDeLicenca(bool Valida, bool ExisteNestaVersao, string Mensagem);
