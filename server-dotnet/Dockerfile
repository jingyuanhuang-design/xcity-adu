# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY XBuildApi/XBuildApi.csproj XBuildApi/
RUN dotnet restore XBuildApi/XBuildApi.csproj

# Copy everything else and build
COPY . .
WORKDIR /src/XBuildApi
RUN dotnet build XBuildApi.csproj -c Release -o /app/build
RUN dotnet publish XBuildApi.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000

ENTRYPOINT ["dotnet", "XBuildApi.dll"]
