---
tags: [prometheushub, vendas, mercado-livre, dashboard, analytics]
status: production-ready
updated: 2026-09-07
---

# Dashboard de vendas

## Objetivo

Dar ao cliente uma visão comercial consolidada e manter `Meus Pedidos` como visão operacional detalhada.

## Fonte dos dados

O Mercado Livre fornece pedido, item/variação, `seller_sku`, quantidade, preço unitário, comissão, valor total, pagamento e datas. A sincronização usa notificações e reconciliação periódica:

- ciclo normal: últimos 2 dias;
- reconciliação noturna: últimos 7 dias;
- sincronização manual: backfill dos últimos 365 dias, com chamadas remotas concorrentes e gravações EF sequenciais;
- a busca agora percorre todas as páginas de até 50 resultados, limitada defensivamente a 10.000 pedidos por conexão/ciclo;
- o Mercado Livre permite consultar pedidos mantidos por até 12 meses; o histórico maior depende do armazenamento local contínuo.

## Modelo estruturado

### `marketplace_orders`

- `channel_created_at`: data real da venda no canal;
- `currency_id`;
- `total_amount`;
- `paid_amount`.

### `marketplace_order_items`

- `channel_sku`: SKU informado pelo canal;
- `sabr_variant_sku`: SKU interno mapeado;
- `product_name`;
- `currency_id`;
- `unit_price`;
- `full_unit_price`;
- `gross_price`;
- `sale_fee`;
- `quantity` continua sendo a quantidade efetivamente vendida.

A migration `AddMarketplaceSalesAnalytics` retroalimenta pedidos antigos a partir dos payloads JSON imutáveis, com conversões defensivas. Novas sincronizações preenchem os campos diretamente.

## Contrato

`GET /api/v1/client/dashboard/sales`

Parâmetros:

- `from`: início ISO-8601;
- `to`: fim ISO-8601;
- `provider`: opcional (`MercadoLivre`, `TikTokShop`, `Shopee`).

Regras:

- período padrão: 30 dias;
- período máximo por consulta: 366 dias;
- comparação automática com período anterior de mesma duração;
- receita considera somente pedidos `paid`; pedidos parcialmente reembolsados permanecem visíveis no detalhamento de status sem inflar o faturamento confirmado;
- valor após taxas = valor total confirmado menos comissões identificadas;
- agrupamento prefere `sabr_variant_sku`, usa `channel_sku` como fallback e sinaliza `SEM-SKU`.

Resposta contém:

- pedidos totais/pagos/cancelados;
- unidades;
- vendas confirmadas, taxas, valor após taxas e ticket médio;
- variação percentual de pedidos e receita;
- série diária;
- ranking dos 10 SKUs;
- distribuição por status;
- unidades sem mapeamento e última sincronização.

## Experiência do cliente

A página inicial apresenta:

1. radar de receita e pulso de sincronização;
2. filtros de 7, 30, 90 dias e 12 meses, além do canal;
3. indicadores comerciais;
4. gráfico diário;
5. distribuição de status;
6. ranking por SKU;
7. skeleton loading, falha acionável e empty states;
8. layout responsivo, foco visível e respeito a `prefers-reduced-motion`.

`Meus Pedidos` continua mostrando o pedido individual, etapas, pacote, etiqueta, rastreamento, quantidade e agora valor vendido por item quando disponível.

## Arquivos centrais

- `src/Phub.Application/Services/ClientSalesDashboardService.cs`
- `src/Phub.Api/Controllers/ClientSalesDashboardController.cs`
- `src/Phub.Infrastructure/Integrations/MercadoLivre/MercadoLivreApiClient.cs`
- frontend: `src/app/client/client-dashboard.*`
- frontend: `src/app/core/services/client-sales-dashboard.service.ts`

## Testes essenciais

- agregar apenas pedidos do cliente autenticado;
- excluir outro cliente mesmo no mesmo tenant;
- quantidade e receita por SKU;
- lista completa de todos os produtos vendidos no período, com busca e paginação;
- itens sem SKU permanecem separados por `item_id` e variação do canal, evitando totais agrupados incorretamente em uma única linha;
- taxas e líquido;
- status cancelado;
- build cliente/admin;
- migration consistente com o snapshot.
