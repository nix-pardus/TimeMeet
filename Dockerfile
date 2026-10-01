# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY TimeMeet.Domain/TimeMeet.Domain.csproj TimeMeet.Domain/
COPY TimeMeet.Application/TimeMeet.Application.csproj TimeMeet.Application/
COPY TimeMeet.Infrastructure/TimeMeet.Infrastructure.csproj TimeMeet.Infrastructure/
COPY TimeMeet.Web/TimeMeet.Web.csproj TimeMeet.Web/
RUN dotnet restore TimeMeet.Web/TimeMeet.Web.csproj

COPY . .
RUN dotnet publish TimeMeet.Web/TimeMeet.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TimeMeet.Web.dll"]
