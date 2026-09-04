# Imagem do serviço de conversão DOCX -> PDF (Syncfusion DocIO + DocIORenderer).
#
# Referência: SyncfusionExamples/Word-Processor-Server-Docker (Dockerfile em dois
# estágios, SDK compila e a imagem final é só o runtime ASP.NET). A diferença é
# que aqui NÃO reimplementamos o Word Processor: este binário só converte para PDF.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Restore separado do resto do código: enquanto o .csproj não muda, o Docker
# reaproveita a camada e o rebuild não baixa os pacotes Syncfusion de novo.
COPY src/JuriusPdfService.csproj ./
RUN dotnet restore JuriusPdfService.csproj

COPY src/ ./
RUN dotnet publish JuriusPdfService.csproj -c Release -o /app --no-restore


FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final

# libfontconfig1 é EXIGIDO pelo SkiaSharp, que é quem o DocIORenderer usa para
# desenhar. Sem ele o serviço sobe e só quebra na primeira conversão.
#
# As fontes importam tanto quanto o código: o PDF é paginado com a fonte que o
# container ENCONTRAR. Sem as substitutas métricas abaixo, o DocIO troca por
# qualquer coisa disponível, as larguras mudam e as quebras de linha (e de página)
# saem em lugar diferente do Word — que é justamente o defeito que não podemos ter
# num documento que vai ser assinado.
#   fonts-liberation -> Arial, Times New Roman e Courier New (métricas idênticas)
#   curl             -> usado só pelo healthcheck do compose
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      libfontconfig1 \
      fontconfig \
      fonts-liberation \
      curl \
 && rm -rf /var/lib/apt/lists/*

# Carlito ~ Calibri e Caladea ~ Cambria (os padrões do Word moderno), também
# métricamente compatíveis. Ficam num RUN separado e tolerante: se o repositório
# da distro não tiver os pacotes, o build NÃO falha — perde-se fidelidade nessas
# duas fontes e o aviso fica no log da build.
RUN apt-get update \
 && (apt-get install -y --no-install-recommends fonts-crosextra-carlito fonts-crosextra-caladea \
     || echo "AVISO: Carlito/Caladea indisponíveis nesta distro; Calibri/Cambria serão substituídas por aproximação.") \
 && rm -rf /var/lib/apt/lists/* \
 && fc-cache -f

WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
# Declaradas vazias para ficarem visíveis em `docker inspect`; os valores reais
# vêm do compose (.env.server / variáveis do stack no Portainer).
ENV SYNCFUSION_LICENSE_KEY=""
ENV PDF_API_KEY=""

EXPOSE 8080
ENTRYPOINT ["dotnet", "JuriusPdfService.dll"]
