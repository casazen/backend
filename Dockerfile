FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Directory.Build.props sets the NuGet vulnerability gate (FD-02): restore fails on High/Critical advisories.
COPY ["Directory.Build.props", "./"]
COPY ["Casazen.Web/Casazen.Web.csproj", "Casazen.Web/"]
COPY ["Casazen.Core/Casazen.Core.csproj", "Casazen.Core/"]
COPY ["Casazen.Infrastructure/Casazen.Infrastructure.csproj", "Casazen.Infrastructure/"]

RUN dotnet restore "Casazen.Web/Casazen.Web.csproj"

COPY . .
WORKDIR "/src/Casazen.Web"
RUN dotnet build "Casazen.Web.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Casazen.Web.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=publish /app/publish .

# Required by System.Net.Mail.SmtpClient on Linux for SMTP authentication.
# Without this lib, SmtpClient hangs/timeouts when connecting to Gmail/any SMTP.
# Ubuntu security mirrors can briefly serve an index from the previous publication.
# Keep signature and checksum verification enabled on both official mirrors.
RUN (apt-get -o APT::Update::Error-Mode=any -o Acquire::Retries=3 update || { \
        find /etc/apt -type f \( -name '*.list' -o -name '*.sources' \) \
          -exec sed -i 's|https\?://security\.ubuntu\.com/ubuntu|https://archive.ubuntu.com/ubuntu|g' {} +; \
        apt-get -o APT::Update::Error-Mode=any -o Acquire::Retries=3 update; \
    }) && apt-get -o Acquire::Retries=3 install -y --no-install-recommends libgssapi-krb5-2 tesseract-ocr tesseract-ocr-ita tesseract-ocr-eng poppler-utils util-linux && rm -rf /var/lib/apt/lists/*

# Railway (and most cloud providers) terminate TLS at the edge.
# The container listens on plain HTTP on PORT (default 8080).
ENV ASPNETCORE_URLS=http://+:8080
ENV PORT=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Casazen.Web.dll"]
