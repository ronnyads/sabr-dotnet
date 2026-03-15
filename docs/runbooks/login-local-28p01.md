# Runbook - Login local falhando por `28P01` (Postgres)

## Sintoma
- Frontend mostra `API local indisponivel em http://127.0.0.1:5250`.
- API encerra no startup com erro `28P01` (`password authentication failed for user "sabr_user"`).

## Causa
- A API nao consegue autenticar no Postgres local (`sabr_user` / `sabr3`).

## Passos de correcao
1. Em sessao superuser do PostgreSQL (pgAdmin ou `psql`), redefina o usuario de app:
   ```sql
   ALTER ROLE sabr_user WITH LOGIN PASSWORD '04692021RGjl!';
   GRANT ALL PRIVILEGES ON DATABASE sabr3 TO sabr_user;
   ```
2. Valide a credencial de app:
   ```bash
   psql -h 127.0.0.1 -p 5432 -U sabr_user -d sabr3 -c "select current_user, current_database();"
   ```
3. Configure a API para usar a credencial local via user-secrets:
   ```bash
   dotnet user-secrets set "ConnectionStrings:Default" "Host=127.0.0.1;Port=5432;Database=sabr3;Username=sabr_user;Password=04692021RGjl!" --project src/Sabr.Api/Sabr.Api.csproj
   ```
4. Suba API e valide health:
   ```bash
   dotnet run --project src/Sabr.Api/Sabr.Api.csproj
   ```
   Health esperado:
   - `http://127.0.0.1:5250/health/ready` => `200`

## Seed e validacao de login
1. Rodar seed de desenvolvimento:
   ```bash
   dotnet run --project src/Sabr.Api/Sabr.Api.csproj -- --seed-dev-initial
   ```
2. Validar login client (`localhost:4200`) com conta seed.
3. Validar login admin (`localhost:4300`) com conta seed.

## Prevencao
- Use bootstrap local completo:
  - `npm run start:client:stack` no `sabr-frontend`.
- Use check rapido antes do Angular:
  - `npm run check:api`
- Sempre manter `ConnectionStrings:Default` do ambiente local em user-secrets, sem hardcode no repo.
