FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY C0302_HoangThai/C0302_HoangThai.csproj C0302_HoangThai/
RUN dotnet restore C0302_HoangThai/C0302_HoangThai.csproj --runtime linux-x64
COPY C0302_HoangThai/ C0302_HoangThai/
RUN dotnet publish C0302_HoangThai/C0302_HoangThai.csproj --configuration Release \
    --runtime linux-x64 --self-contained false --no-restore --output /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends libtesseract5 tesseract-ocr-eng tesseract-ocr-vie fontconfig fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=10000 \
    OMP_THREAD_LIMIT=1 \
    Ocr__TessDataPath=/usr/share/tesseract-ocr/5/tessdata \
    Ocr__ValidateOnStartup=true
EXPOSE 10000
USER $APP_UID
ENTRYPOINT ["dotnet", "C0302_HoangThai.dll"]
