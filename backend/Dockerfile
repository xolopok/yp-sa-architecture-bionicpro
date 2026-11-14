FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY BionicPro.Reports.Api.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish BionicPro.Reports.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:8000
EXPOSE 8000

ENTRYPOINT ["dotnet", "BionicPro.Reports.Api.dll"]
