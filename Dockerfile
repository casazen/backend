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

ENV ASPNETCORE_URLS=https://+:5001
EXPOSE 5001

ENTRYPOINT ["dotnet", "Casazen.Web.dll"]
