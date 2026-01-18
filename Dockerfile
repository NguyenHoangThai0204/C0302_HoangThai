FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["C0302_HoangThai.csproj", "./"]
RUN dotnet restore "C0302_HoangThai.csproj"
COPY . .
RUN dotnet build "C0302_HoangThai.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "C0302_HoangThai.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "C0302_HoangThai.dll"]