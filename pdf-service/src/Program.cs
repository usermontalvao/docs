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

using System.Reflection;
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

// ---------------------------------------------------------------------------
// O diagnóstico é feito por REFLEXÃO, de propósito.
//
// `Platform` e `ValidateLicense` mudaram de forma entre versões do Syncfusion:
// a 31.x trocou o enum de plataformas de UI para edições de produto, e a 34.1.29
// removeu membros. Escrever `Platform.WordToPDF` direto amarra a COMPILAÇÃO a
// nomes que podem não existir — e um serviço que não compila não diagnostica
// nada. Aqui, um nome ausente vira resposta ("não existe nesta versão"), e uma
// API ausente vira `apiDisponivel: false` com o motivo. Nunca um build quebrado.
//
// `SyncfusionLicenseProvider` em si continua ligado em tempo de compilação: ele
// já era usado no `RegisterLicense` e comprovadamente existe.
// ---------------------------------------------------------------------------
var tipoProvider = typeof(SyncfusionLicenseProvider);
var tipoPlatform = tipoProvider.Assembly.GetType("Syncfusion.Licensing.Platform");

// Aceita as QUATRO formas que a API já teve: escalar ou array, com ou sem a
// mensagem de saída. A 34.1.29 introduziu a variante em array, e assumir só a
// escalar foi o que fez o diagnóstico anterior dizer "não existe".
MethodInfo? validarMetodo = null;
var validarUsaArray = false;
var validarTemMensagem = false;
var motivoIndisponivel = string.Empty;

// Inventário do que REALMENTE existe no provider. Se nada casar, é isto que
// transforma a próxima tentativa em conserto em vez de palpite.
var assinaturasValidateLicense = new List<string>();
var metodosDoProvider = new List<string>();

if (tipoPlatform is null)
{
    motivoIndisponivel = "enum Syncfusion.Licensing.Platform não existe nesta versão.";
}
else
{
    var tipoPlatformArray = tipoPlatform.MakeArrayType();

    foreach (var metodo in tipoProvider.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
    {
        metodosDoProvider.Add($"{(metodo.IsPublic ? "public" : "internal")} {metodo.Name}({string.Join(", ", metodo.GetParameters().Select(par => (par.IsOut ? "out " : string.Empty) + par.ParameterType.Name))})");

        if (metodo.Name != "ValidateLicense")
        {
            continue;
        }

        var parametros = metodo.GetParameters();
        assinaturasValidateLicense.Add(string.Join(", ", parametros.Select(par => (par.IsOut ? "out " : string.Empty) + par.ParameterType.Name)));

        var primeiroEhPlatform = parametros.Length >= 1
            && (parametros[0].ParameterType == tipoPlatform || parametros[0].ParameterType == tipoPlatformArray);
        if (!primeiroEhPlatform)
        {
            continue;
        }

        var temMensagem = parametros.Length == 2 && parametros[1].IsOut;
        if (parametros.Length != 1 && !temMensagem)
        {
            continue;
        }

        // Prefere a sobrecarga que explica o motivo; entre iguais, fica a primeira.
        if (validarMetodo is null || (temMensagem && !validarTemMensagem))
        {
            validarMetodo = metodo;
            validarUsaArray = parametros[0].ParameterType == tipoPlatformArray;
            validarTemMensagem = temMensagem;
        }
    }

    if (validarMetodo is null)
    {
        motivoIndisponivel = assinaturasValidateLicense.Count > 0
            ? "ValidateLicense existe, mas nenhuma sobrecarga casa com Platform/Platform[] — ver assinaturasValidateLicense."
            : "SyncfusionLicenseProvider.ValidateLicense não existe nesta versão — ver metodosDoProvider.";
    }
}

var apiDisponivel = motivoIndisponivel.Length == 0;

// Invoca a validação para UM valor do enum, montando o argumento na forma que a
// sobrecarga encontrada exige (escalar ou array de UM elemento, tipado).
Func<object, (bool Valida, string Mensagem)> validar = valor =>
{
    if (validarMetodo is null || tipoPlatform is null)
    {
        return (false, motivoIndisponivel);
    }

    try
    {
        object primeiroArgumento;
        if (validarUsaArray)
        {
            // Array.CreateInstance e não object[]: o tipo do array precisa ser
            // exatamente Platform[], senão o Invoke recusa por incompatibilidade.
            var vetor = Array.CreateInstance(tipoPlatform, 1);
            vetor.SetValue(valor, 0);
            primeiroArgumento = vetor;
        }
        else
        {
            primeiroArgumento = valor;
        }

        if (validarTemMensagem)
        {
            var args = new object?[] { primeiroArgumento, null };
            var ok = (bool)(validarMetodo.Invoke(null, args) ?? false);
            return (ok, Redigir(args[1] as string, licenca));
        }

        return ((bool)(validarMetodo.Invoke(null, new[] { primeiroArgumento }) ?? false), string.Empty);
    }
    catch (Exception erro)
    {
        // Uma plataforma que explode não pode derrubar o diagnóstico inteiro.
        return (false, Redigir((erro.InnerException ?? erro).Message, licenca));
    }
};

// Existir no enum é pergunta SEPARADA de estar licenciado: mesmo sem a API de
// validação, saber se `WordToPDF` é um nome válido nesta versão já informa.
Func<string, bool> existeNoEnum = nome =>
{
    if (tipoPlatform is null)
    {
        return false;
    }

    try
    {
        return Enum.IsDefined(tipoPlatform, nome);
    }
    catch
    {
        return false;
    }
};

// As três que decidem se este serviço pode existir sem marca d'água.
var focos = new Dictionary<string, ResultadoDeLicenca>(StringComparer.Ordinal);
foreach (var nome in new[] { "WordToPDF", "Word", "WordEditor" })
{
    var existe = existeNoEnum(nome);
    if (!existe)
    {
        focos[nome] = new ResultadoDeLicenca(false, false, $"Platform.{nome} não existe nesta versão.");
        continue;
    }

    if (!apiDisponivel)
    {
        focos[nome] = new ResultadoDeLicenca(false, true, motivoIndisponivel);
        continue;
    }

    var (valida, mensagem) = validar(Enum.Parse(tipoPlatform!, nome));
    focos[nome] = new ResultadoDeLicenca(valida, true, mensagem);
}

// Mapa completo: é ele que diz QUAL edição comprar, se faltar alguma.
var cobertura = new SortedDictionary<string, bool>(StringComparer.Ordinal);
if (apiDisponivel && tipoPlatform is not null)
{
    foreach (var valor in Enum.GetValues(tipoPlatform))
    {
        if (valor is null)
        {
            continue;
        }

        cobertura[valor.ToString() ?? "?"] = validar(valor).Valida;
    }
}

var listaCobertas = cobertura.Where(par => par.Value).Select(par => par.Key).ToArray();

app.Logger.LogInformation(
    "Licença: chavePresente={presente} nChaves={n} DocIORenderer={versao} apiDeValidacao={api}",
    chavePresente,
    quantidadeDeChaves,
    versaoDocIORenderer,
    apiDisponivel ? "disponível" : motivoIndisponivel);

foreach (var par in focos)
{
    app.Logger.LogInformation(
        "Licença[{plataforma}]: valida={valida} existeNestaVersao={existe} {mensagem}",
        par.Key,
        par.Value.Valida,
        par.Value.ExisteNestaVersao,
        par.Value.Mensagem);
}

app.Logger.LogInformation(
    "Licença: plataformas cobertas = {cobertas}",
    listaCobertas.Length > 0 ? string.Join(", ", listaCobertas) : "NENHUMA");

if (!chavePresente)
{
    app.Logger.LogWarning("SYNCFUSION_LICENSE_KEY vazia: o PDF sai com marca d'água de avaliação.");
}
else if (apiDisponivel && !focos["WordToPDF"].Valida)
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
    apiDeValidacaoDisponivel = apiDisponivel,
    motivoIndisponivel,
    assinaturasValidateLicense,
    metodosDoProvider,
    focos = focos.ToDictionary(
        par => par.Key,
        par => new { par.Value.Valida, par.Value.ExisteNestaVersao, par.Value.Mensagem }),
    plataformasCobertas = listaCobertas,
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
