---
tags: [prometheushub, catalogo, estoque, marketplace, invariantes]
updated: 2026-09-08
---

# Catálogo e estoque omnichannel

## Separação do domínio

- `Product` e `ProductVariant`: produto e SKU mestre do HUB.
- `Publication`/rascunho: produto preparado pelo cliente.
- `TenantMarketplaceListingMap`: vínculo entre SKU mestre e identidade externa do anúncio.
- `MarketplaceOrderItem`: snapshot imutável do vínculo resolvido na entrada do pedido.
- `MarketplaceOperationJob`: trabalho remoto persistente e rastreável.

## Invariantes

1. SKU mestre não é editável pelo cliente; trocar significa remapear anúncios futuros.
2. Auto-mapping exige uma única correspondência normalizada, ativa e autorizada.
3. Remapeamento não altera pedidos, reservas ou auditoria históricos.
4. Estoque anunciado é `max(0, physicalStock - reservedStock - safetyBuffer)`.
5. Reserva é atômica: bloqueia as variantes afetadas, valida disponibilidade e salva saldo/versão na mesma transação.
6. Toda mudança de saldo incrementa `inventoryVersion`.
7. Antes de cada escrita remota o processo relê a versão; trabalho antigo é superseded.
8. Tenant, cliente, seller e integração são sempre validados em conjunto.

## Acesso ao catálogo

`Public` fica disponível para qualquer cliente aprovado. `PlanRestricted` exige catálogo ligado a plano e assinatura válida. A migration cria o Catálogo Público padrão e associa produtos ativos.

## Vínculo pendente e pagamento

- O editor de vínculo é oferecido dentro do item do pedido para Mercado Livre e demais providers normalizados; o cliente escolhe uma variante autorizada do catálogo sem editar o SKU mestre.
- Ao criar um vínculo manual, somente itens ainda não resolvidos com a mesma identidade externa recebem o snapshot do mapping. Itens já resolvidos e pedidos históricos permanecem imutáveis.
- Pedidos pendentes afetados são reconciliados imediatamente para criar a reserva de estoque. O pagamento repete a reconciliação de forma idempotente antes de consumir o saldo, cobrindo pedidos importados antes do vínculo.
- Pagamento permanece bloqueado para item sem mapping, falta de estoque ou cancelamento pendente. No portal, os bloqueadores são apresentados antes da ação.
- Ao consumir reservas duplicadas ou antigas, o débito físico é limitado à quantidade efetiva dos itens do pedido; todo excesso reservado é liberado e não pode baixar estoque duas vezes.
- Em **Meus Produtos**, “Vincular existente” abre um seletor dos anúncios reais da conexão autorizada. A busca retorna no máximo 200 opções normalizadas, separa variações e mostra imagem, Item ID, SKU do canal, preço, estoque, status e vínculo atual antes da confirmação.
- `GET /api/v1/client/integrations/mercadolivre/seller-listings` nunca aceita credenciais ou cliente informados pela UI: tenant e cliente vêm da sessão e o seller precisa pertencer à conexão autorizada.

## Anúncios normalizados e Capability Engine

- `MarketplaceListingIdentity` reúne `itemId`, `variationId`, `userProductId`, seller, integração e provider sem expor o JSON bruto do canal.
- `ClientMarketplaceListing` é o contrato comum consumido pelo portal.
- `MercadoLivreLegacyListingAdapter` e `MercadoLivreUserProductListingAdapter` traduzem os dois modelos para o mesmo domínio.
- `MarketplaceListingCapabilities` é calculado no backend e informa, por campo, permissão, motivo do bloqueio e valor atual.
- A interface nunca decide sozinha se pode editar. A sincronização relê o anúncio, recalcula capacidades e compara `mappingVersion` e `evaluationHash`.
- SKU mestre e estoque nunca são editados como campo de anúncio. SKU usa remapeamento auditado; estoque usa o ledger central.
- Mudanças autorizadas são registradas como `MarketplaceListing.SynchronizeChanges` em `AuditEvents`.
- O `evaluationHash` contém uma janela de validade determinística de cinco minutos. Mesmo sem outra alteração, uma avaliação vencida não autoriza escrita.
- A revisão é persistida como job `LISTING_CHANGE` no estado `DRAFT`. A confirmação só aceita o mesmo tenant, cliente, anúncio e rascunho ainda aberto; depois termina em `COMPLETED`, `SUPERSEDED` ou `FAILED`.
- Salvar o rascunho registra `MarketplaceListing.SaveChangeDraft`; confirmar registra a auditoria da sincronização.

Endpoints do portal:

- `GET /api/v1/client/marketplace-mappings/{id}/listing`
- `POST /api/v1/client/marketplace-mappings/{id}/listing/changes`
- `POST /api/v1/client/marketplace-mappings/{id}/listing/drafts`
- `POST /api/v1/client/marketplace-mappings/{id}/listing/drafts/{draftId}/apply`

## Fila de estoque

- Alterações de disponibilidade criam jobs `SYNC_STOCK` em `MarketplaceOperationJob`.
- `dedupeKey` consolida provider, integração, anúncio, variação e `inventoryVersion` com índice único parcial no PostgreSQL.
- A inserção usa `ON CONFLICT DO NOTHING`, portanto chamadas concorrentes são idempotentes.
- O worker relê `inventoryVersion` antes de obter o token e novamente antes da escrita remota.
- Versão antiga, vínculo removido ou seller fora da liberação gradual termina em `SUPERSEDED` e nunca escreve no canal.
- `GlobalInventoryWrite=false` permanece como padrão; somente sellers piloto configurados entram na fila de escrita.
- Legacy Item usa `PUT /items/{itemId}` com `available_quantity`; variação Legacy usa o mesmo recurso com `variations: [{ id, available_quantity }]`. User Product consulta primeiro `/user-products/{id}/stock` e exige o `x-version` retornado antes de escrever em `seller_warehouse`.
- Em múltiplos depósitos, a nova quantidade é distribuída proporcionalmente ao saldo local atual, com total exato e desempate determinístico por `store_id`. Zerar o SKU zera todos os depósitos administráveis.
- Localizações exclusivamente `meli_facility` são observadas como estoque Full gerenciado pelo Mercado Livre e nunca recebem escrita indevida.

## Rollback

As migrations são aditivas. Em incidente, interromper workers/escrita remota antes de reverter a aplicação. Não apagar snapshots históricos nem diminuir `inventoryVersion`; manter colunas novas até a versão anterior voltar a operar com segurança.
