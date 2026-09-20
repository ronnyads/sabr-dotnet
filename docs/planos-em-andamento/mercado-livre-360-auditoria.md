# Auditoria de baseline — Mercado Livre 360

Data: 20/09/2026. Autor: Claude, a pedido de Ronildo, antes de iniciar qualquer alteração de código do plano `mercado-livre-360.md`.

Esta auditoria confirma, lendo o código atual do `sabr-dotnet` (branch `main`, commit `ad3f4b4`), quais achados do apêndice do plano ainda procedem e levanta achados novos não listados lá. Não houve nenhuma alteração de código nesta etapa. O repositório `sabr-frontend` foi liberado para leitura, mas a varredura das telas Angular (AdminProducts, wizard de publicação) não foi concluída por tempo — fica como pendência explícita, não como "auditado e aprovado".

## 1. Já corrigido (confirmado lendo o código atual)

| Item do apêndice | Commit | Verificação |
|---|---|---|
| Consulta PostgreSQL com `ANY`/`string[]` (erro 42809) na reconciliação de reservas | `4cf6856` | `MarketplaceOrderInventoryService.ReconcileReservationsCoreAsync` converte os SKUs para JSON e usa `jsonb_array_elements_text` em vez de `ANY(string[])`. |
| Reserva de pedidos com envio terminal | `f75ec67` | `ReconcileReservationsCoreAsync` já verifica `terminalOrder`/`terminalShipment` antes de manter a reserva. |
| Identidade de anúncio separada do SKU interno / SKU duplicado | `5212a8c`, `f97935c`, `ad3f4b4` | Confirmado por leitura de `MercadoLivreCatalogImportService` e do histórico de commits. |

## 2. Achados novos (não estavam no apêndice original), por severidade

### 2.1 [CORRIGIDO em 20/09/2026] Crítico — `ExpireReservationsAsync` liberava reserva de pedido ainda ativo por timer

`MercadoLivreSyncService.ExpireReservationsAsync` (linha 233) roda periodicamente via `MarketplaceSyncWorker` e libera **qualquer** `StockReservation` cujo `ExpiresAt` já passou, sem checar se o pedido correspondente ainda está ativo/aguardando pagamento. Isso contraria diretamente a regra que você mesmo definiu na seção 2 do plano: *"O bloqueio operacional de uma venda ativa não será liberado automaticamente por um timer. Cancelamento confirmado ou resolução administrativa auditada encerra esse bloqueio."*

Efeito prático: um pedido pago no Mercado Livre mas ainda não pago no Prometheus pode ter seu estoque interno liberado automaticamente após o TTL (hoje 24h, definido em `MarketplaceOrderCheckoutService.ConfirmAsync`), permitindo que o mesmo SKU seja vendido para outro pedido enquanto o primeiro comprador ainda tem uma venda confirmada no canal. Isso é uma via direta para overselling e para repetir, de forma diferente, o mesmo tipo de incidente registrado em 19/09 na nota do Ledger (que tratou apenas pedidos com **envio terminal**, não pedidos simplesmente **aguardando pagamento**).

Este é, na minha avaliação, o achado de maior risco financeiro/operacional de toda a auditoria, porque roda em produção agora, silenciosamente, a cada ciclo do worker.

**Evidência da correção (20/09/2026):** `MarketplaceOrderWorkflow.IsTerminalOrderStatus` (novo helper) + `MarketplaceOrderInventoryService.IsReservationTerminalAsync` (novo método público, extraído da checagem que já existia em `ReconcileReservationsCoreAsync`) + `MercadoLivreSyncService.ExpireReservationsAsync` agora só libera reservas órfãs (pedido não existe mais) ou de pedidos/envios já terminais; reservas de pedido ativo são mantidas e logadas (`_logger.LogInformation`), nunca liberadas por timer. Teste novo: `ExpireReservations_KeepsReservedStock_ForActiveOrder` (tests/Phub.Api.Tests/Integration/MercadoLivreIntegrationHttpTests.cs). O teste existente foi renomeado para `ExpireReservations_ReleasesReservedStock_ForTerminalOrder_AndSyncsAvailability` para deixar explícito que cobre apenas pedido terminal.

**Limitação de verificação:** não foi possível rodar `dotnet build`/`dotnet test` neste ambiente (VM do dispositivo sem SDK .NET; container de nuvem com SDK instalado mas `dotnet restore` bloqueado por política de rede em `api.nuget.org`, erro `NU1301`). A revisão foi manual (diff linha a linha, balanceamento de chaves, conferência de assinaturas). Além disso, o pipeline `.github/workflows/backend-deploy.yml` filtra testes de integração (`--filter "FullyQualifiedName!~Integration"`), então o novo teste de regressão **não roda no CI** — só localmente. Recomendado rodar `dotnet test --filter "FullyQualifiedName~ExpireReservations"` localmente para confirmar antes de considerar 100% validado.

### 2.2 [CORRIGIDO em 20/09/2026] Crítico — `MarketplaceOrderCheckoutService.LoadVariantsAsync` usava `ANY({0})` com `string[]` cru

Mesmo padrão que gerou o erro PostgreSQL 42809 documentado na nota do Ledger e corrigido em `4cf6856` — mas o fix só foi aplicado em `MarketplaceOrderInventoryService`. Em `MarketplaceOrderCheckoutService.cs:354`, o lock de variantes durante a confirmação de pagamento ainda faz:

```csharp
.FromSqlRaw("SELECT * FROM product_variants WHERE variant_sku = ANY ({0}) ORDER BY variant_sku FOR UPDATE", skus)
```

Esse é exatamente o caminho citado no apêndice do plano ("`LoadVariantsAsync` com `ANY({0})`/`string[]`"). Como esse método roda dentro de `ConfirmAsync` — ou seja, toda confirmação de pagamento interno com PostgreSQL real — o risco é de falha no fluxo de pagamento, não apenas na reconciliação em background.

**Evidência da correção (20/09/2026):** substituí `ANY({0})` por `WHERE variant_sku IN (SELECT jsonb_array_elements_text({0}::jsonb))`, serializando os SKUs com `JsonSerializer.Serialize`, exatamente a mesma técnica já usada em `MarketplaceOrderInventoryService.ReconcileReservationsCoreAsync`. Diferente do achado 2.1, aqui consegui validar a própria consulta contra um PostgreSQL 16 real (instância local isolada, fora do banco de produção): testei a query nova com `PREPARE`/`EXECUTE` usando um parâmetro de texto contendo JSON (`$1::jsonb`), cobrindo lista populada, lista vazia (0 linhas, sem erro) e a combinação com `FOR UPDATE` — todos os casos executaram corretamente. Isso não substitui rodar a suíte de testes .NET (que continua bloqueada por falta de acesso ao NuGet neste ambiente), mas reduz bastante o risco de erro de sintaxe/semântica SQL nesta mudança específica.

**Limitação:** a suíte de testes de integração usa `UseInMemoryDatabase`, então nenhum teste automatizado exercita este caminho `FromSqlRaw`/Npgsql-específico (nem antes nem depois da correção) — só é exercitado com PostgreSQL real em produção/homologação. Recomendo rodar manualmente `dotnet test` local e, se possível, um teste manual do fluxo de confirmação de pagamento contra um PostgreSQL de homologação antes de considerar 100% validado.

### 2.3 Alto — `FinancialSyncJobService.ProcessNextAsync` libera o lease sem compare-and-set do dono

O bloco `finally` (linha 178-185) sempre zera `LockedBy`/`LeaseUntil`, mesmo que o lease já tenha expirado e outro worker tenha reclamado o job nesse meio-tempo. Um worker atrasado que termina depois pode sobrescrever o progresso do worker que already reclamou o job. Falta um `WHERE LockedBy = @workerId` na escrita final.

### 2.4 Alto — `StockAvailabilityService.ProcessStockJobAsync` não revalida a mapping atual, só a versão de estoque

Reconfirma `InventoryVersion` antes de cada tentativa (correto), mas nunca confere se a mapping ainda aponta para o `SabrVariantSku` do payload. Um remapeamento entre o enfileiramento e o processamento do job pode gravar o estoque do SKU errado no anúncio do canal, porque o job busca a mapping fresca pelo `Id`, mas usa o SKU capturado no payload no momento do enfileiramento.

### 2.5 Médio — `MercadoLivreWebhookService`: sincronização por janela inteira, sem heartbeat de crash

O serviço já processa por tópico/recurso específico (melhor do que o apêndice sugeria), mas: (a) após validar o recurso, ele dispara `MercadoLivreSyncService.SyncNowAsync` para o seller inteiro, não uma sincronização pontual do recurso notificado; (b) o claim usa `ExecuteUpdateAsync` condicionado a `Status IN (Pending, Failed)` — um evento que trava em `Processing` porque o worker caiu no meio nunca mais é reclamado, porque `Processing` não está entre os status elegíveis para reclaim e não há lease/heartbeat.

### 2.6 Médio — `FinancialProfitabilityService`: soma entre moedas e divergência global não pareada

- `gross`, `externalNet`, `productCost` (e portanto `profit`) somam `AmountCents` de todas as `activeEntries` sem agrupar por `CurrencyId` — se um seller tiver entradas em moedas diferentes, os totais viram uma mistura sem sentido.
- `Divergence.AbsoluteCents` é `sum(confirmedTotal) - sum(estimatedTotal)` calculado sobre **todas** as entradas estimadas/confirmadas, não apenas as chaves econômicas que têm os dois lados. Isso contraria a regra do plano: "Calcular divergência apenas entre estimativa e confirmação correspondentes. Falta de confirmação não equivale a diferença negativa." (O `componentDeltas`, usado só para o detalhamento por componente, já faz o pareamento certo — o total exibido no topo, não.)

### 2.7 Baixo/Médio — `MercadoLivreCatalogImportService.ImportAsync`: `ItemIds` vazio ainda pode selecionar tudo

`requestedItemIds.Count == 0 || requestedItemIds.Contains(...)` deixa passar todos os anúncios quando `ItemIds` vem vazio, inclusive fora do modo `PreviewOnly`. Não há validação que exija seleção explícita antes de uma gravação real, como a nota do Obsidian descreve ("nenhuma gravação acontece antes da seleção explícita"). Vale adicionar essa validação explicitamente, mesmo que hoje o frontend sempre mande a lista.

## 3. Confirmado como já implementado corretamente (não mexer sem necessidade)

- `MercadoLivreCatalogImportService.ImportAsync`: já separa "vincular existente" (não toca estoque) de "criar novo" (aplica estoque padrão só na criação) — o apêndice descrevia isso como um problema, mas o comportamento documentado na nota 06 do Obsidian já é este por design.
- `FinancialLedgerService`: append-only, chave de idempotência com dupla checagem sob advisory lock, cabeça única por identidade econômica — bate com os invariantes do Ledger.
- `MarketplaceOrderCheckoutService.ConfirmAsync`: transação única cobrindo reserva → carteira → ledger → pedido, com hash de cotação e proteção de repetição idempotente (`SabrPaymentConfirmedAt.HasValue`).
- `SentinelService` / adapters de capacidade (`MarketplaceListingAdapters`): estrutura bate com a nota 07/08 do Obsidian (capacidades calculadas no backend, estados internos/externos separados).

## 4. Confirmado como realmente ausente (o próprio plano já sabia)

- Nenhuma classe de Claims/Returns/Refund no backend — ciclo de devoluções não existe.
- Nenhum consumidor de Billing conciliatório — só entidades/probes de OAuth.
- Nenhuma projeção diária de analytics (`DailyProjection`/`ProjecaoDiaria` não existe).
- Nenhuma estrutura de `ClientPurchase`/unidades pré-compradas — a reserva comprada descrita na seção 2 do plano ainda não tem modelo de dados.

## 5. Pendências desta auditoria (não verificado)

- Frontend (`sabr-frontend`): telas AdminProducts, wizard de publicação e Meus Produtos não foram lidas a fundo — a varredura de arquivos expirou por tempo. Os achados de "Cadastro administrativo" e parte de "Publicação"/"Edição de anúncios" continuam como estavam no apêndice do plano, sem confirmação de código nesta rodada.
- `ListingDraftService.cs` tem 4472 linhas; só foi confirmado, por grep, que ele e `MercadoLivrePublishService` continuam ligados a controllers separados (`ClientListingDraftsController` e `ClientMercadoLivreIntegrationController`) — ou seja, os dois caminhos de publicação concorrentes citados no apêndice ainda coexistem. O conteúdo interno de `ListingDraftService` não foi lido linha a linha.
- `MercadoLivreMappingService.CreateAsync` (fluxo antigo de vínculo manual) não faz nenhuma validação de anúncio remoto, autorização do produto, ou auditoria — apenas confere se a variante existe e persiste. Isso confirma o achado do apêndice, mas não foi comparado a fundo com o fluxo mais novo usado pelo editor de vínculo dentro do item do pedido (nota 07), que pode já ter suplantado este caminho na prática.

## 6. Recomendação de prioridade para a próxima etapa (correções críticas)

Por risco decrescente:

1. ~~`ExpireReservationsAsync` liberando reserva de pedido ativo por timer (2.1)~~ — **corrigido em 20/09/2026**, ver evidência acima. Pendente apenas confirmação de build/teste local (rede bloqueada neste ambiente).
2. ~~`LoadVariantsAsync` do checkout com `ANY({0})`/`string[]` (2.2)~~ — **corrigido em 20/09/2026**, ver evidência acima (validado contra PostgreSQL real, mas não pelo dotnet test).
3. Compare-and-set de lease no `FinancialSyncJobService` (2.3).
4. Revalidação de mapping no `StockAvailabilityService.ProcessStockJobAsync` (2.4).
5. Lease/heartbeat e sincronização pontual no webhook (2.5).
6. Segregação de moeda e divergência pareada no `FinancialProfitabilityService` (2.6).
7. Validação de `ItemIds` na importação de catálogo (2.7).

Cada item seria implementado como incremento verificável isolado, com teste e evidência, atualizando esta auditoria e as notas do Obsidian afetadas, conforme o próprio plano exige.
