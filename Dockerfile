# CodeContextService - multi-stage build for GCP (Cloud Run / GKE)
# Requirements: MSBuild (for Roslyn MSBuildWorkspace), LibGit2Sharp native libs (linux-x64)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["CodeContextService/CodeContextService.csproj", "CodeContextService/"]
COPY ["RoslynTools/RoslynTools.csproj", "RoslynTools/"]
RUN dotnet restore "CodeContextService/CodeContextService.csproj"

COPY . .
WORKDIR /src/CodeContextService
# Restore + publish with linux-x64 so LibGit2Sharp native binaries are included (restore must use same RID)
RUN dotnet publish -c Release -r linux-x64 -o /app/publish

# Use SDK image as runtime: Roslyn MSBuildWorkspace needs MSBuild at runtime (MSBuildLocator.RegisterDefaults)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Ephemeral clone dir: Cloud Run's writable filesystem is under /tmp
ENV RepoClone__CloneRootPath=/tmp/repo-clones
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV ASPNETCORE_ENVIRONMENT=Production
# Show detailed circuit errors in browser (set to false in production if you want to hide stack traces)
ENV CircuitOptions__DetailedErrors=true
# Ensure .NET uses the shared framework in this image (no roll-forward needed)
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080
ENTRYPOINT ["dotnet", "CodeContextService.dll"]
