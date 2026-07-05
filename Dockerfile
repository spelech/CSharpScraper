# Stage 1: Build the C# application as self-contained
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Copy csproj and restore dependencies for linux-x64
COPY playwright-csharp-scraper.csproj ./
RUN dotnet restore -r linux-x64

# Copy source and publish self-contained
COPY . ./
RUN dotnet publish -c Release -o /app -r linux-x64 --self-contained true --no-restore

# Stage 2: Final runtime image (Use pre-baked Playwright noble image with browsers)
FROM mcr.microsoft.com/playwright:v1.49.1-noble AS final
WORKDIR /app

# Copy the self-contained app from the build stage
COPY --from=build /app .

# Set up environment variables
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

# Execute the self-contained binary directly
ENTRYPOINT ["./playwright-csharp-scraper"]
