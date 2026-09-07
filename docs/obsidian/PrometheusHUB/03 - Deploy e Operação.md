---
tags: [prometheushub, deploy, flyio, cloudflare]
updated: 2026-09-07
---

# Deploy e operação

## Backend

Push em `main` executa `.github/workflows/backend-deploy.yml`:

1. restore;
2. build Release;
3. testes fora da pasta/namespace Integration;
4. deploy remoto no Fly app `sabr-api-dev`.

A API detecta migrations pendentes no startup e executa `Database.MigrateAsync()`. Toda migration precisa ser aditiva, retrocompatível e segura para reinício com API/worker concorrentes.

## Frontend

Push em `main` executa `.github/workflows/deploy.yml`:

1. build cliente de produção;
2. build admin de produção;
3. deploy do cliente no Cloudflare Pages `sabr-frontend`;
4. deploy do admin no Cloudflare Pages `sabr-admin`.

## Checklist pós-deploy

- workflow do backend verde;
- health da API respondendo;
- migration aplicada;
- endpoint autenticado `/api/v1/client/dashboard/sales` respondendo;
- workflow do frontend verde;
- `app.marketplaceonline.site` e `admin.marketplaceonline.site` com HTTP 200;
- login e dashboard do cliente sem erro de console/rede;
- confirmar que API e worker continuam ativos no Fly.

## Rollback

- frontend: redeploy do commit estável no Cloudflare Pages;
- backend: restaurar release anterior do Fly;
- migrations desta área são aditivas. Não remover colunas em rollback emergencial; manter o banco compatível e reverter apenas aplicação.
