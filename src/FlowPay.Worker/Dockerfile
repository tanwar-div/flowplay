FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/FlowPay.Domain/FlowPay.Domain.csproj src/FlowPay.Domain/
COPY src/FlowPay.Application/FlowPay.Application.csproj src/FlowPay.Application/
COPY src/FlowPay.Infrastructure/FlowPay.Infrastructure.csproj src/FlowPay.Infrastructure/
COPY src/FlowPay.Worker/FlowPay.Worker.csproj src/FlowPay.Worker/

RUN dotnet restore src/FlowPay.Worker/FlowPay.Worker.csproj

COPY src/ src/

RUN dotnet publish src/FlowPay.Worker/FlowPay.Worker.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_gcServer=1

COPY --from=build /app/publish ./

# The .NET base images already provide a non-root `app` user and expose its id as
# APP_UID. Using it beats creating one: the API needs no privileged access, and running
# as root would hand an attacker who found a remote code execution bug the whole
# container.
USER $APP_UID

ENTRYPOINT ["dotnet", "FlowPay.Worker.dll"]
