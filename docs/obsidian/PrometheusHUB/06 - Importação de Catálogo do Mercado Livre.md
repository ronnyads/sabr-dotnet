---
tags: [prometheushub, mercado-livre, catalogo, sku]
updated: 2026-09-19
---

# Importação de catálogo do Mercado Livre

O admin pode importar produtos a partir da integração Mercado Livre de um cliente. A prévia traz o catálogo conectado sem restringir marca; o admin escolhe exatamente quais anúncios importar. Novos produtos recebem estoque físico inicial de 1.000 unidades. O nome da ação é genérico porque sérum é apenas uma categoria de produto.

## Regras

- `seller_custom_field`/`SELLER_SKU`, `itemId`, `variationId` e `userProductId` são identificadores externos. O Item ID `MLB...` nunca é criado automaticamente como SKU mestre.
- Cada anúncio/variação selecionado exige um SKU interno explícito, salvo quando o SKU do canal já corresponde a uma variante interna ativa e inequívoca. Sem ponte válida, o anúncio é ignorado com aviso; pedidos sem vínculo continuam bloqueados para pagamento.
- O produto e a variante usam o mesmo SKU quando o anúncio não possui variações.
- Cada atribuição declara `createNewProduct`: vincular SKU existente é o padrão seguro; criar SKU novo exige intenção explícita e Preço Catálogo positivo, global ou por linha, que representa o custo cobrado ao seller. Um erro de digitação não cria produto automaticamente. O preço de venda do ML é apenas informativo.
- Cada variação do anúncio pode apontar a uma variação interna distinta. Um SKU base que já possui variações não pode receber vínculo direto sem a escolha da variante.
- Produtos legados cujo SKU mestre já é `MLB...` não são renomeados automaticamente. O admin atribui um novo SKU interno na importação; o vínculo passa a usá-lo apenas para pedidos futuros, enquanto o cadastro legado permanece para preservar referências históricas. A desativação do legado exige revisão de reservas/publicações.
- Todo produto ativo é vinculado ao **Catálogo Público** por padrão. Catálogos `PlanRestricted` continuam dependentes de uma assinatura ativa; clientes aprovados enxergam o catálogo público mesmo sem plano.
- Cada item/variação do Mercado Livre recebe um `TenantMarketplaceListingMap`, inclusive anúncios espelhados/sincronizados, para garantir o reconhecimento dos pedidos.
- A operação é idempotente e gera o evento auditável `AdminProducts.ImportFromMercadoLivre`.

## Interface e endpoint

- Admin > cliente > integração Mercado Livre > **Buscar produtos do ML** abre uma prévia. O admin filtra, seleciona produtos agrupados por SKU e importa somente os escolhidos.
- `POST /api/v1/admin/tenants/{tenantSlug}/clients/{clientId}/integrations/mercadolivre/catalog/import`.
- O request aceita busca, marcas, estoque, modo de prévia e `skuAssignments` (`itemId`, `variationId`, `internalSku`, `createNewProduct`, `catalogPriceCents` opcional). A interface envia as atribuições de SKU por anúncio/variação e permite custo individual com fallback para o custo padrão.
- `ItemIds` limita a gravação aos anúncios selecionados. A prévia pode consultar tudo, mas nenhuma gravação acontece antes da seleção explícita.

## Inteligência de seller

- O mesmo painel permite consultar anúncios públicos de outro vendedor pelo Seller ID e termo opcional.
- A consulta usa a rota suportada `/users/{sellerId}/items/search`, seguida da leitura paralela dos detalhes dos itens, e exibe título, marca, preço e item ID. O termo opcional é aplicado localmente sobre título, marca e item ID.
- É uma operação somente leitura; não acessa dados privados, pedidos, estoque real ou credenciais do seller pesquisado.
- As leituras de detalhes usam concorrência limitada para reduzir o tempo de espera sem provocar uma rajada descontrolada de requisições à API.

## Observação operacional

A importação cria o catálogo e os mappings. O auto-mapping só é aceito quando o SKU normalizado corresponde a um único SKU mestre ativo e autorizado. Ausência ou ambiguidade mantém o item pendente.

Cada item de pedido grava um snapshot imutável do mapping (`mapping_snapshot_id`, versão, motivo e instante). Remapear um anúncio só altera pedidos recebidos depois da mudança; pedidos e reservas históricos não são reescritos.

Os mappings armazenam identidades Legacy (`itemId`/`variationId`) e `userProductId`, permitindo que os adaptadores do Mercado Livre evoluam sem vazar o formato bruto para o domínio.
