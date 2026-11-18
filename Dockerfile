# syntax=docker/dockerfile:1
# One build stage for the whole solution, three small runtime images (targets):
#   service  – AcceptCrypto.Service (API, payment page, chain scanner, webhook dispatcher)
#   demoshop – AcceptCrypto.DemoShop (sample merchant app on the SDK)
#   payer    – scripted payer CLI `pay` (TEST ONLY, local chain); also deploys the mock USDC (`setup`)
# Used by docker-compose.yml; build one with e.g. `docker build --target service -t acceptcrypto-service .`

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet restore src/AcceptCrypto.Service \
 && dotnet restore src/AcceptCrypto.DemoShop \
 && dotnet restore src/AcceptCrypto.Payer
RUN dotnet publish src/AcceptCrypto.Service  -c Release --no-restore -o /out/service \
 && dotnet publish src/AcceptCrypto.DemoShop -c Release --no-restore -o /out/demoshop \
 && dotnet publish src/AcceptCrypto.Payer    -c Release --no-restore -o /out/payer

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS service
WORKDIR /app
COPY --from=build /out/service .
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AcceptCrypto.Service.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS demoshop
WORKDIR /app
COPY --from=build /out/demoshop .
ENV Shop__DataPath=/data/shop.json
# The data volume must be writable by the non-root app user.
USER root
RUN mkdir -p /data && chown $APP_UID /data
USER $APP_UID
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "AcceptCrypto.DemoShop.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS payer
WORKDIR /app
COPY --from=build /out/payer .
ENTRYPOINT ["dotnet", "pay.dll"]
