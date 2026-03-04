# PR2 UI v1.2 - Aceite Objetivo (Passa/Nao passa)

Data da execucao: 2026-02-18

## 1) Resultado geral

- Trilha automatica (Fase A): `PASS`
- Trilha manual UI+Network (Fase B): `PENDENTE (obrigatoria para GO)`

GO final somente com Fase A `PASS` + Fase B `PASS`.

---

## 2) Fase A - Pre-gate tecnico (automatico)

### 2.1 Build/test

1. `dotnet test SabrHub.sln -v minimal` -> `PASS`
   - Resultado: `Aprovado: 118, Falha: 0`
2. `dotnet test tests/Sabr.Api.Tests/Sabr.Api.Tests.csproj --filter FullyQualifiedName~ListingDraftHttpTests -v minimal` -> `PASS`
   - Resultado: `Aprovado: 25, Falha: 0`
3. `npm.cmd run build:client` -> `PASS`
4. `npm.cmd run build:admin` -> `PASS`

### 2.2 Verificacao estrutural (codigo)

1. Rotas client novas -> `PASS`
   - `sabr-frontend/src/app/app.routes.client.ts`: `publications`, `publications/new`, `publications/:draftId`
2. Menu Publicacoes -> `PASS`
   - `sabr-frontend/src/app/client/client-shell.ts`: item de menu condicionado por `environment.ui.publicationsEnabled`
3. Feature flags -> `PASS`
   - `sabr-frontend/src/environments/environment.ts`: `publicationsEnabled=true`, `mlLegacyPublishBlock=false`
   - `sabr-frontend/src/environments/environment.production.ts`: `publicationsEnabled=false`, `mlLegacyPublishBlock=false`
4. Endpoints usados pela UI PR2 -> `PASS`
   - `sabr-frontend/src/app/core/services/publications.service.ts`:
     - `POST /api/v1/client/listings/drafts/upsert`
     - `POST /api/v1/client/listings/drafts/get`
     - `POST /api/v1/client/listings/drafts/validate`
     - `POST /api/v1/client/listings/drafts/publish`
     - `POST /api/v1/client/listings/publications/query`
     - `POST /api/v1/client/marketplaces/categories/attributes`
     - `POST /api/v1/client/marketplaces/fees/estimate`
   - `sabr-frontend/src/app/core/services/mercado-livre-integration.service.ts`:
     - `GET /api/v1/client/integrations/mercadolivre/status`
5. Bloco legado de publish em Integracoes -> `PASS` (sem ocorrencia textual)
   - Arquivos verificados:
     - `sabr-frontend/src/app/client/client-ml-integration.ts`
     - `sabr-frontend/src/app/client/client-ml-integration.html`

---

## 3) Fase B - Gate manual UI+Network (obrigatorio)

Status atual: `PENDENTE`

### Gate 1 - Build correto sem cache

- [ ] Abrir em aba anonima + hard refresh
- [ ] Confirmar menu `Publicacoes` visivel com flag ligada
- Resultado: `PENDENTE`
- Evidencia esperada: print menu lateral com item `Publicacoes`

### Gate 2 - Rotas novas

- [ ] `/client/publications` renderiza lista e chama `POST /listings/publications/query`
- [ ] `/client/publications/new` renderiza wizard e chama `GET /integrations/mercadolivre/status`
- [ ] Autosave chama `POST /listings/drafts/upsert` e retorna `draftId/rowVersion`
- [ ] `/client/publications/:draftId` carrega draft via `POST /listings/drafts/get`
- Resultado: `PENDENTE`
- Evidencia esperada: print/gif + Network das chamadas acima

### Gate 3 - Meus Produtos

- [ ] Badge ML por linha
- [ ] Botao `Publicar` por linha
- [ ] Navegacao para wizard com `variantSku` na query
- [ ] Sem N+1 (chamada batch para status)
- Resultado: `PENDENTE`
- Evidencia esperada: print Meus Produtos + Network

### Gate 4 - Wizard validate/publish

- [ ] `Validar` chama `POST /listings/drafts/validate` com `{draftId}`
- [ ] Exibe `issues[]` com `fieldPath`, `step`, `severity`
- [ ] `Publicar` chama `POST /listings/drafts/publish` com `{draftId,rowVersion}`
- [ ] Sucesso retorna novo `rowVersion`, `publishedItemId`, `publishedPermalink`
- Resultado: `PENDENTE`
- Evidencia esperada: gif curto do fluxo + response payload no Network

### Gate 5 - Concorrencia (2 abas)

- [ ] Mesmo draft em 2 abas
- [ ] Aba A autosave atualiza rowVersion
- [ ] Aba B publica com versao antiga
- [ ] Recebe `409 DRAFT_CONCURRENCY_CONFLICT` e UI orienta recarregar
- Resultado: `PENDENTE`
- Evidencia esperada: print Network do 409 + mensagem na UI

### Gate 6 - Integracoes limpa

- [ ] Tela Integracoes sem bloco legado de publish/dry-run
- [ ] Mantem conexao/sync/status/mappings/pedidos
- Resultado: `PENDENTE`
- Evidencia esperada: print da tela Integracoes sem bloco legado

---

## 4) Matriz final de aceite

| Gate | Status | Evidencia |
|---|---|---|
| Fase A (auto) | PASS | Build/test + verificacao estrutural (secoes 2.1/2.2) |
| Gate 1 | PENDENTE | print_menu_publicacoes.png |
| Gate 2 | PENDENTE | gif_rotas_publicacoes.gif + network_rotas.png |
| Gate 3 | PENDENTE | print_meus_produtos_badges.png + network_batch_badges.png |
| Gate 4 | PENDENTE | gif_wizard_validate_publish.gif + network_publish_success.png |
| Gate 5 | PENDENTE | network_409_concurrency.png + print_toast_conflito.png |
| Gate 6 | PENDENTE | print_integracoes_sem_legado.png |

---

## 5) Comandos executados (raw)

```bash
dotnet test SabrHub.sln -v minimal
dotnet test tests/Sabr.Api.Tests/Sabr.Api.Tests.csproj --filter FullyQualifiedName~ListingDraftHttpTests -v minimal
npm.cmd run build:client
npm.cmd run build:admin
```

