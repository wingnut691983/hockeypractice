# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first, as its own layer, so code edits don't re-download packages.
COPY global.json ./
COPY HockeyPractice/HockeyPractice.csproj HockeyPractice/
RUN dotnet restore HockeyPractice/HockeyPractice.csproj

COPY HockeyPractice/ HockeyPractice/
RUN dotnet publish HockeyPractice/HockeyPractice.csproj -c Release -o /app --no-restore

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app ./

# UpTurtle wires its Service to targetPort 8080 and injects no PORT variable.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

# The platform forces uid 1000 regardless; setting it here keeps build-time file
# ownership matching the runtime user.
USER 1000

# Exec form, not shell form: a shell would swallow SIGTERM and Kubernetes would end up
# SIGKILLing the app mid-request on every rolling deploy. ASP.NET Core drains in-flight
# requests on SIGTERM by default, well inside the 30s grace window.
ENTRYPOINT ["dotnet", "HockeyPractice.dll"]
