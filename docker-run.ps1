# Build and run CodeContextService in Docker for local testing.
# Use http://127.0.0.1:8080 in your browser (localhost may hang due to IPv6 on Windows).

$ErrorActionPreference = "Stop"
$image = "codecontextservice"
$name = "codecontextservice"
$port = "8080"

Write-Host "Building Docker image..." -ForegroundColor Cyan
docker build -t $image .

docker stop $name 2>$null
docker rm $name 2>$null

Write-Host "Starting container..." -ForegroundColor Cyan
docker run -d --name $name -p "127.0.0.1:${port}:8080" -v "codecontextservice-dp-keys:/root/.aspnet/DataProtection-Keys" -e "JwtSettings__Key=dev-key-min-32-chars-for-hmac-sha256!!" -e "UserCredentials__0__Username=admin" -e "UserCredentials__0__Password=changeme" -e "UserCredentials__0__Role=Admin" $image

Write-Host ""
Write-Host "Open http://127.0.0.1:${port} in your browser. Login: admin / changeme" -ForegroundColor Green
Write-Host "Logs: docker logs -f $name" -ForegroundColor Gray
