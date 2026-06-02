FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

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

# Railway (and most cloud providers) terminate TLS at the edge.
# The container listens on plain HTTP on PORT (default 8080).
ENV ASPNETCORE_URLS=http://+:8080
ENV PORT=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Casazen.Web.dll"]
