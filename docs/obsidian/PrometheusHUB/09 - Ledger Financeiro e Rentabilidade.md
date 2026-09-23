# Ledger Financeiro e Rentabilidade

## Objetivo

Explicar, sem sobrescrita de fatos, quanto o seller faturou, quais débitos incidiram, qual foi o custo operacional e qual é a maturidade de cada resultado.

## Camadas

- `OPERATIONAL`: projeção imediata de Orders, Discounts, Shipments, Packs e catálogo.
- `RECONCILED`: confirmação posterior pelos recursos oficiais de Billing.
- `INTERNAL_CONFIRMED`: fatos confirmados dentro do PrometheusHUB, como o débito do custo de produto.

Billing confirma e concilia; nunca substitui Orders e Shipments como fonte da operação.

## Invariantes

1. Receita/crédito/recuperação são positivos; custo/tarifa/débito/reembolso são negativos.
2. O ledger é append-only. Uma correção cria uma nova entrada e avança `FinancialEconomicHead`.
3. Há exatamente uma cabeça ativa por tenant, cliente, provider, seller e `economicKey`.
4. Somente a cabeça ativa participa dos agregados.
5. `economicOccurredAt`, `financialConfirmedAt` e `observedAt` são datas distintas.
6. `sellerId` é dimensão relacional explícita em fatos, projeções, cursores, jobs e índices.
7. Refund ou chargeback sem alocação oficial permanece no grão fornecido; nunca há rateio inventado.
8. Entrega de devolução não recupera custo. A recuperação exige confirmação administrativa de item vendável.
9. Preço de catálogo é fotografado no pedido e não muda retroativamente.
10. Ausência de dado não equivale a zero.
11. Corrigido em 20/09/2026 (achado 2.6 da auditoria `mercado-livre-360-auditoria.md`): `FinancialProfitabilityService.GetAsync` somava `AmountCents` de todas as chaves econômicas estimadas/confirmadas para compor `Divergence.AbsoluteCents`, mesmo quando uma chave só tinha um dos dois lados — violando esta mesma regra (item 10). Agora `estimatedTotal`/`confirmedTotal` só acumulam chaves com estimativa **e** confirmação, no mesmo laço que já faz esse pareamento para `componentDeltas`. Também passou a agrupar `gross`, `externalNet`, `productCost` e `ReconciledConfirmedValueCents` pela moeda dominante (`sameCurrencyEntries`) em vez de somar `AmountCents` entre moedas diferentes. Cobertura: `Profitability_DivergenceOnlyCountsKeysWithBothEstimateAndConfirmation` e `Profitability_KeepsTotalsInOneCurrency_WhenEntriesAreMixed` (`tests/Phub.Api.Tests/FinancialLedgerServiceTests.cs`).
12. Custo de fornecedor externo é uma fonte própria, versionada por anúncio/variação e seller; nunca usa preço de venda nem `catalogPriceSnapshot` interno. A versão é resolvida pela data econômica e fotografada no item.
13. Quando uma venda passa a cancelada, receita, desconto comercial, tarifa da venda, refund correlato e custo do produto recebem nova cabeça `VOIDED`. O valor original permanece no ledger para auditoria, mas a cabeça `VOIDED` não participa de agregados. Frete efetivamente cobrado, compensação, claim, ajuste e frete de retorno permanecem como fatos econômicos independentes; por isso um pedido cancelado pode ter resultado negativo. Reabrir a venda cria nova versão ativa, sem reescrever o histórico.

## Equação operacional por item e pedido

`marketplaceNetAmount = grossRevenue - marketplaceFees - sellerShipping - refunds ± adjustments`.
`productCost = Σ(catalogPriceSnapshot × quantity)`.
`operationalProfit = marketplaceNetAmount - productCost`.
`operationalMarginPct = operationalProfit / grossRevenue × 100`; faturamento zero deixa a margem não calculável.

O custo vem apenas do SKU interno e do preço de catálogo fotografado. Preço de venda do anúncio nunca é fallback. SKU/custo ausente torna o pedido incompleto, sem zero inventado. O custo estimado passa a confirmado após débito interno por nova entrada append-only, preservando o snapshot. A UI distingue resultado parcial de valor confirmado e expõe a composição da conta.

Para produtos explicitamente classificados como externos, o custo vem da versão externa informada pelo cliente. Sem versão vigente, o item fica `EXTERNAL_COST_PENDING` e é divulgado fora dos totais de vendas/lucro. Com custo vigente, receita, custo e margem entram normalmente. Alterar o custo cria nova versão com vigência atual; vendas anteriores mantêm o snapshot antigo. Valores de pedido/frete/reembolso sem alocação oficial continuam no grão de origem e nunca são rateados artificialmente.

## Maturidade

- `INCOMPLETO`: falta SKU, custo, frete, alocação ou componente obrigatório.
- `ESTIMADO`: componentes operacionais resolvidos, ainda sem confirmação suficiente.
- `PARCIALMENTE_CONFIRMADO`: confirmação parcial.
- `CONFIRMADO`: todos os componentes esperados confirmados ou não aplicáveis.
- `REABERTO`: fato tardio alterou um pedido antes confirmado.

## Sincronização

- Webhook permanece primário.
- O catch-up operacional de até 365 dias é particionado em chunks de no máximo 31 dias.
- Cada chunk possui checkpoint, dedupe key, tentativas, lease e retomada durável.
- A execução de um chunk avança no máximo uma hora por tentativa. O checkpoint só avança após persistir a sincronização dessa hora; uma interrupção repete apenas a hora incompleta. Timeout HTTP sem cancelamento do worker gera `RETRY`, não deixa o chunk em `RUNNING`.
- O catch-up nunca reserva estoque de pedidos com envio externo terminal (`shipped`, `delivered`, `returned`, `cancelled`, `not_delivered`) ou pedido cancelado/reembolsado. A sincronização usa o reconciliador transacional de reservas; mapeamentos legados sem `integrationId` são fallback somente dentro do mesmo tenant, cliente, provider e seller.
- Incidente de 19/09/2026: o worker foi pausado e 172 reservas indevidas de pedidos terminais (183 unidades, sellers 2496573592 e 3630972063) foram liberadas transacionalmente, preservando as linhas históricas com status `Released`. `reservedStock`, `availableStock` e `inventoryVersion` foram atualizados por SKU; verificação posterior encontrou zero reservas terminais e zero disponibilidades inconsistentes. Retomar o worker somente após deploy da regra preventiva.
- Regra preventiva implementada em 20/09/2026 (achado 2.1 da auditoria `mercado-livre-360-auditoria.md`): `MercadoLivreSyncService.ExpireReservationsAsync` deixou de liberar por TTL qualquer `StockReservation` vencida; agora só libera reservas órfãs (pedido inexistente) ou de pedidos já terminais, via `MarketplaceOrderInventoryService.IsReservationTerminalAsync` (extraído do reconciliador transacional, reaproveitando `MarketplaceOrderWorkflow.IsTerminalOrderStatus`). Reservas de pedido ainda ativo (ex.: `paid`, `created`) são mantidas e contadas em log; só cancelamento confirmado ou resolução administrativa auditada as encerra. Cobertura: teste de integração `ExpireReservations_KeepsReservedStock_ForActiveOrder` (novo) e `ExpireReservations_ReleasesReservedStock_ForTerminalOrder_AndSyncsAvailability` (renomeado do teste anterior). Build/teste local não pôde ser executado neste ambiente por bloqueio de rede a `api.nuget.org`; pendente confirmação de `dotnet test` local antes de considerar 100% verificado.
- O lock de variantes no reconciliador usa um parâmetro JSON convertido em conjunto de SKUs no PostgreSQL; não passar `string[]` diretamente ao `ANY` via `FromSqlRaw`, pois esse caminho gerou `42809` em produção. A consulta parametrizada foi validada contra PostgreSQL com `PREPARE`/`EXECUTE` e transação revertida.
- O mesmo padrão `ANY({0})`/`string[]` cru existia em `MarketplaceOrderCheckoutService.LoadVariantsAsync` (lock de variantes na confirmação de pagamento) e foi corrigido em 20/09/2026 com a mesma técnica (`jsonb_array_elements_text`). Validado com `PREPARE`/`EXECUTE` contra PostgreSQL 16 real (instância isolada), cobrindo lista populada, lista vazia e `FOR UPDATE`; não coberto por teste .NET porque a suíte usa `UseInMemoryDatabase`.
- Corrigido em 20/09/2026 (achado 2.5, item b): `MercadoLivreWebhookService.ProcessPendingEventsAsync` não reclamava um evento travado em `PROCESSING` (worker morto no meio do processamento) porque só `Pending`/`Failed` eram elegíveis para reclaim. Adicionada janela de staleness de 20 minutos (`ProcessingStaleAfter`), mesmo padrão de lease de `FinancialSyncJob`/`SentinelReconciliationService`. Validado com `PREPARE`/`EXECUTE` contra PostgreSQL real: `PROCESSING` velho é reclamado, `PROCESSING` recente não é. Item (a) do mesmo achado — sincronizar só o recurso notificado em vez do seller inteiro — segue pendente (mudança maior, sem risco de corretude, só de eficiência).
- Um `RUNNING` com lease vencido pode ser reclamado por outro worker. `lockedBy` e `leaseUntil` identificam a tentativa ativa; nunca limpar manualmente o lease de uma tentativa ainda viva.
- Em 20/09/2026, o processamento de `orders_v2` passou a sincronizar somente o pedido identificado e já validado pelo webhook, reutilizando o pipeline de upsert, mapping, reserva e projeção financeira. O sync dirigido revalida o seller do pedido e não atualiza `LastSyncAt` da janela inteira. Webhooks de payment/shipment mantêm temporariamente a reconciliação por seller até terem associação inequívoca ao pedido. Um teste de integração força falha na busca ampla e confirma que o pedido do webhook ainda é importado.
- Corrigido em 20/09/2026: a liberação final do lease em `FinancialSyncJobService.ProcessNextAsync` agora exige `WHERE Id = jobId AND LockedBy = workerId` (via `ExecuteUpdateAsync` em PostgreSQL, com fallback equivalente para teste). Antes, um worker atrasado (a chamada de sync fica fora da transação e pode passar dos 30 minutos do lease) podia sobrescrever todo o estado de um job já reclamado por outro worker, não só o lease. Validado com UPDATE guardado contra PostgreSQL real (escrita tardia = 0 linhas afetadas, dono legítimo = 1 linha). O mesmo padrão foi encontrado, com risco bem menor, em `SentinelReconciliationService.EnqueueDueAsync` (achado 2.8 da auditoria) — corrigido em 20/09/2026: a liberação final agora exige `WHERE Id = candidate.Id AND LockedBy = _workerId`; validado contra PostgreSQL real (liberação pelo dono = 1 linha, tentativa tardia após outro worker já ter reclamado = 0 linhas, lock do outro worker preservado). Sem teste de integração em C# porque o método usa `ExecuteUpdateAsync` sem nenhum branch InMemory-safe, ao contrário do `FinancialSyncJobService`.
- A aquisição PostgreSQL usa `FOR UPDATE SKIP LOCKED`; a chamada HTTP ocorre fora da transação.
- Retentativas usam backoff exponencial com full jitter.
- Billing é processado sequencialmente por seller, grupo e período, preservando `from_id`; respostas parciais e rate limit não viram zero confirmado.

## APIs

- `GET /api/v1/client/dashboard/profitability`
- `GET /api/v1/client/dashboard/profitability/orders`
- `GET /api/v1/client/dashboard/profitability/orders/{orderId}`
- `POST /api/v1/client/dashboard/sync`
- `GET /api/v1/client/dashboard/sync/{jobId}`
- `GET /api/v1/client/dashboard/sync-status`
- `GET|PUT /api/v1/client/financial-settings/tax`
- `GET|POST /api/v1/admin/tenants/{tenantSlug}/clients/{clientId}/integrations/mercadolivre/financial-capabilities[/probe]`
- `GET /api/v1/admin/financial-reconciliation/runs`

## Rollout e recuperação

As migrações são aditivas. Ativar primeiro em shadow mode, auditar grants separados do Mercado Livre e Mercado Pago e validar manualmente a amostra do seller piloto. Qualquer centavo sem causa identificada bloqueia o rollout. Em incidente, pausar consumidores financeiros; o fluxo operacional de pedidos continua independente e as filas retomam do checkpoint.

## Autorização Mercado Pago (preparação)

- Aplicação separada da integração Mercado Livre; nunca reutilizar o token ML como se fosse MP.
- Configurar `MercadoPago__ClientId`, `MercadoPago__ClientSecret`, `MercadoPago__RedirectUri=https://api.marketplaceonline.site/api/v1/client/integrations/mercadopago/callback` e `MercadoPago__ClientPortalBaseUrl=https://app.marketplaceonline.site` como secrets do backend. Não registrar valores secretos neste vault ou no Git.
- Registrar exatamente o mesmo redirect URI na aplicação Mercado Pago correta. O cliente inicia em Integrações → Mercado Livre → Conectar Mercado Pago.
- O callback exige que o `user_id` autorizado corresponda ao seller de uma conexão ML do mesmo tenant/cliente. O grant é armazenado criptografado em `MarketplaceOAuthGrant` com `AppFamily=MERCADO_PAGO`.
- `Conta autorizada` significa somente que o OAuth concluiu. `billingMercadoPago` continua falso até um probe real dos recursos Billing e a conciliação; o dashboard não deve chamar valores estimados de confirmados.
- `PA_UNAUTHORIZED_RESULT_FROM_POLICIES` no probe indica que o provider negou a consulta de Billing apesar do OAuth válido. Conferir a permissão funcional de Faturamento na aplicação correta, renovar a autorização e repetir o probe. Não converter essa falha em valor confirmado zero.
- Ao trocar o segredo da aplicação, revisar grants e solicitar nova autorização. Não reutilizar grants de outro aplicativo.

## Verificação de acesso Billing ML (20/09/2026)

- O admin `POST /api/v1/admin/tenants/{tenantSlug}/clients/{clientId}/integrations/mercadolivre/financial-capabilities/probe` resolve e valida explicitamente o tenant do contexto administrativo, depois consulta `GET /billing/integration/monthly/periods?group=ML&document_type=BILL&limit=1` com a credencial da conexão ML do seller. O endpoint é usado somente como teste de acesso, nunca como origem operacional nem como conciliação.
- Uma conexão OAuth ou `/users/me` bem-sucedido não implica permissão Billing. A capacidade `billingMercadoLivre` só fica verdadeira após resposta JSON válida de Billing (`results` array) verificada nas últimas 24 horas, registrada no grant de metadados ML por tenant, cliente e seller. Esse registro não duplica tokens; a credencial operacional permanece na conexão ML.
- 403 e respostas inválidas deixam a capacidade não verificada. 429/5xx/erro transitório preservam o último resultado comprovado e expõem a pendência de integração. Nenhum desses casos registra lucro confirmado igual a zero.
- Após um 429, probes manuais respeitam cooldown de cinco minutos gravado no metadado do grant; cliques repetidos não renovam a janela nem geram novas chamadas ao Billing. A homologação real de 20/09/2026 no seller 2496573592 encontrou `ML_BILLING_RATE_LIMITED`, com HTTP interno 200 e risco visível no Admin.
- A consulta segue as [boas práticas oficiais de Billing](https://developers.mercadolivre.com.br/pt_br/boas-praticas-para-o-consumo-das-apis-de-relatorios-de-faturamento): Billing é pós-venda e não substitui Orders/Shipments; detalhes futuros deverão usar `from_id`, consumo sequencial e tratamento de `206`/`429`.
- A reconciliação financeira consome detalhes oficiais por pedido em lotes de até 60 IDs, separados por seller. Cada lote é persistido como job idempotente e respeita `Retry-After`, respostas parciais e retomada por lease. Venda bruta, comissão e frete só substituem a estimativa quando a origem fornece correspondência inequívoca; demais cobranças/créditos permanecem no grão do pedido como ajustes não alocados, sem rateio inventado entre SKUs.
- O gate de amostra manual e divergência de centavos permanece obrigatório antes de promover o seller piloto a resultado integralmente confirmado. A implantação pode coletar e comparar os fatos em shadow mode sem esconder pendências.

## Apresentação e verificação financeira (20/09/2026)

- O portal distingue autorização OAuth, acesso aos dados financeiros e conferência concluída. Um `MP_BILLING_RATE_LIMITED` mantém a conta conectada e mostra indisponibilidade temporária, não pedido de reconexão.
- O probe MP guarda uma janela mínima de cinco minutos após HTTP 429; tentativas nesse período não chamam o provedor. O status informa quando tentar novamente. O código técnico fica recolhido na interface.
- A API de rentabilidade expõe bruto, tarifas, frete, refunds, ajustes, líquido do marketplace, custo, lucro, margem e maturidade do custo. O lucro negativo é tratado visualmente como prejuízo estimado e os dados incompletos seguem parciais.
- O custo confirmado após checkout usa uma nova entrada financeira append-only da mesma chave econômica. Nenhum snapshot histórico é modificado.
- Receita bruta usa o valor dos itens antes das deduções; tarifa, frete, reembolso e ajuste são fatos separados com sinais próprios. A soma externa ativa representa o líquido econômico estimado, não necessariamente o valor já disponível na conta Mercado Pago.
- Se o pedido não trouxer preço bruto ou `sale_fee` para algum item, a projeção inclui `GROSS_REVENUE_PENDING` ou `MARKETPLACE_FEE_PENDING` e permanece incompleta; ausência desses dados não significa valor zero. A interface traduz esses motivos para linguagem do seller.
- O líquido efetivamente creditado só recebe status conferido após conciliação dos recursos oficiais. A implementação produz entradas `Reconciled` apenas para componentes com correspondência inequívoca e mantém o restante como pendência ou ajuste não alocado. O indicador integralmente confirmado continua bloqueado até a amostra manual explicar diferenças de competência, liberação, retenções, estornos e eventuais centavos.
