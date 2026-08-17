FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish CafeMiniPdv/CafeMiniPdv.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_USE_POLLING_FILE_WATCHER=true
EXPOSE 8080
ENTRYPOINT ["dotnet", "CafeMiniPdv.dll"]
