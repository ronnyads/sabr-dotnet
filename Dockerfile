FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore "src/Phub.Api/Phub.Api.csproj"
RUN dotnet restore "src/Phub.Worker/Phub.Worker.csproj"
RUN dotnet publish "src/Phub.Api/Phub.Api.csproj" -c Release -o /app/api /p:UseAppHost=false
RUN dotnet publish "src/Phub.Worker/Phub.Worker.csproj" -c Release -o /app/worker /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/api ./api
COPY --from=build /app/worker ./worker
COPY docker-entrypoint.sh /app/docker-entrypoint.sh
RUN chmod +x /app/docker-entrypoint.sh

ENTRYPOINT ["/app/docker-entrypoint.sh"]
CMD ["api"]
