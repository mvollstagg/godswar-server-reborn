# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src
COPY tools/Godswar.WebGmTool/Godswar.WebGmTool.csproj tools/Godswar.WebGmTool/
RUN dotnet restore tools/Godswar.WebGmTool/Godswar.WebGmTool.csproj
COPY tools/Godswar.WebGmTool/ tools/Godswar.WebGmTool/
RUN dotnet publish tools/Godswar.WebGmTool/Godswar.WebGmTool.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Godswar.WebGmTool.dll"]
