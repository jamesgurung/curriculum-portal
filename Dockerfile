ARG DOTNET_VERSION=11.0-preview

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-resolute-chiseled-composite-extra AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY nuget.config .
COPY CustomPackages ./CustomPackages
COPY CurriculumPortal.csproj .
RUN dotnet restore CurriculumPortal.csproj --configfile nuget.config
COPY . .
RUN dotnet build CurriculumPortal.csproj -c Release -o /app/build --no-restore

FROM build AS publish
RUN dotnet publish CurriculumPortal.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
ARG GITHUB_RUN_NUMBER
ENV GITHUB_RUN_NUMBER=$GITHUB_RUN_NUMBER
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CurriculumPortal.dll"]