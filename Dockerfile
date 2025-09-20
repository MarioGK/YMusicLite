# Use the official .NET 10 runtime as a base image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS base
WORKDIR /app
EXPOSE 80

# Install FFMpeg
RUN apt-get update && apt-get install -y ffmpeg && rm -rf /var/lib/apt/lists/*

# Use the .NET SDK for building the app
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy the project file
COPY ["src/YMusicLite/YMusicLite.csproj", "YMusicLite/"]
RUN dotnet restore "YMusicLite/YMusicLite.csproj"

# Copy the rest of the application
COPY src/YMusicLite/ YMusicLite/
WORKDIR "/src/YMusicLite"
RUN dotnet build "YMusicLite.csproj" -c Release -o /app/build

# Publish the application
FROM build AS publish
RUN dotnet publish "YMusicLite.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Final stage/image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create data directory
RUN mkdir -p /app/data

ENTRYPOINT ["dotnet", "YMusicLite.dll"]