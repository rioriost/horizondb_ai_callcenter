FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY AiCallCenter.Api.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends libasound2t64 \
    && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "AiCallCenter.Api.dll"]
