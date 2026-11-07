# syntax=docker/dockerfile:1.7

FROM node:22.23-alpine AS web-build
WORKDIR /source/src/ProjectHub.Web
ENV HUSKY=0
COPY src/ProjectHub.Web/package.json src/ProjectHub.Web/pnpm-lock.yaml src/ProjectHub.Web/pnpm-workspace.yaml ./
RUN corepack enable && corepack prepare pnpm@11.18.0 --activate && pnpm install --frozen-lockfile
COPY src/ProjectHub.Web/ ./
ENV VITE_OUTPUT_DIR=/web-dist
RUN pnpm build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /source
COPY Directory.Build.props global.json ProjectHub.slnx ./
COPY src/ProjectHub.Domain/ProjectHub.Domain.csproj src/ProjectHub.Domain/
COPY src/ProjectHub.Application/ProjectHub.Application.csproj src/ProjectHub.Application/
COPY src/ProjectHub.Infrastructure/ProjectHub.Infrastructure.csproj src/ProjectHub.Infrastructure/
COPY src/ProjectHub.Api/ProjectHub.Api.csproj src/ProjectHub.Api/
RUN dotnet restore src/ProjectHub.Api/ProjectHub.Api.csproj
COPY src/ProjectHub.Domain/ src/ProjectHub.Domain/
COPY src/ProjectHub.Application/ src/ProjectHub.Application/
COPY src/ProjectHub.Infrastructure/ src/ProjectHub.Infrastructure/
COPY src/ProjectHub.Api/ src/ProjectHub.Api/
COPY --from=web-build /web-dist/ src/ProjectHub.Api/wwwroot/
RUN dotnet publish src/ProjectHub.Api/ProjectHub.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    TZ=Asia/Hong_Kong
EXPOSE 8080
COPY --from=api-build /app/publish/ ./
RUN mkdir -p /app/data/project-assets && chown -R $APP_UID /app/data
USER $APP_UID
ENTRYPOINT ["dotnet", "ProjectHub.Api.dll"]
