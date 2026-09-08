---
tags: [prometheushub, deploy, flyio, cloudflare]
updated: 2026-09-08
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
- confirmar que um lote de etiquetas retorna HTTP 202, progride no endpoint de job e é consumido pela máquina `worker`;
- conferir divergências de estoque antes de habilitar escrita global; jobs com `inventoryVersion` inferior à versão corrente não podem chamar o canal.
- no piloto, conferir separadamente anúncios Legacy e User Products; User Products multi-origem devem registrar leitura e escrita com `x-version`, e conflito HTTP 409 deve ser retentado somente após nova leitura.
- não tratar estoque Full (`meli_facility`) como gravável pelo HUB.

## Liberação gradual do estoque

1. aplicar as migrations aditivas;
2. validar o backfill do Catálogo Público e snapshots históricos;
3. observar `physicalStock - reservedStock - safetyBuffer` sem escrita global;
4. liberar uma conta piloto;
5. comparar canal x HUB;
6. expandir somente após ausência de divergências.

O buffer padrão é 2. Reserva, saldo disponível e incremento monotônico de `inventoryVersion` ocorrem na mesma transação. Nunca reduzir a versão manualmente durante rollback.

## Rollback

- frontend: redeploy do commit estável no Cloudflare Pages;
- backend: restaurar release anterior do Fly;
- migrations desta área são aditivas. Não remover colunas em rollback emergencial; manter o banco compatível e reverter apenas aplicação.
