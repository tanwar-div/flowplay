# Build and runtime are separate stages so the shipped image carries the .NET runtime and
# the published output, not the SDK, the NuGet cache and the source tree.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project and props files are copied before the source so that `restore` is cached on the
# dependency graph alone. A code change then rebuilds without re-downloading packages.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/FlowPay.Domain/FlowPay.Domain.csproj src/FlowPay.Domain/
COPY src/FlowPay.Application/FlowPay.Application.csproj src/FlowPay.Application/
COPY src/FlowPay.Infrastructure/FlowPay.Infrastructure.csproj src/FlowPay.Infrastructure/
COPY src/FlowPay.Api/FlowPay.Api.csproj src/FlowPay.Api/

RUN dotnet restore src/FlowPay.Api/FlowPay.Api.csproj

COPY src/ src/

RUN dotnet publish src/FlowPay.Api/FlowPay.Api.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    # Server GC trades a little memory for much lower allocation-pause latency, which is
    # what the p95 target is sensitive to.
    DOTNET_gcServer=1 \
    DOTNET_TieredPGO=1

COPY --from=build /app/publish ./

# The .NET base images already provide a non-root `app` user and expose its id as
# APP_UID. Using it beats creating one: the API needs no privileged access, and running
# as root would hand an attacker who found a remote code execution bug the whole
# container.
USER $APP_UID

EXPOSE 8080

# The readiness endpoint checks the database and broker, so an instance only joins the
# load balancer once it can actually serve payments.
#
# Probed with .NET rather than curl or wget: the runtime image ships neither, and adding
# a package manager layer just to get one would grow the image and its attack surface for
# a single HTTP GET.
HEALTHCHECK --interval=10s --timeout=5s --start-period=25s --retries=5 \
    CMD ["dotnet", "FlowPay.Api.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "FlowPay.Api.dll"]
