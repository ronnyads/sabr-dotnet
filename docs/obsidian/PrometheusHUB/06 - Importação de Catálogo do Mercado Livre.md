---
tags: [prometheushub, mercado-livre, catalogo, sku]
updated: 2026-09-07
---

# Importação de catálogo do Mercado Livre

O admin pode importar produtos a partir da integração Mercado Livre de um cliente. A operação inicial filtra as marcas Boca Rosa e Principia e define estoque físico inicial de 1.000 unidades. O nome da ação é genérico porque sérum é apenas uma categoria de produto.

## Regras

- O SKU vem de `seller_custom_field` ou do atributo `SELLER_SKU`; anúncio sem SKU válido é ignorado e devolvido como aviso.
- O produto e a variante usam o mesmo SKU quando o anúncio não possui variações.
- Produtos já existentes não são duplicados nem têm seus dados comerciais sobrescritos; somente o estoque da variante é atualizado.
- Novos produtos usam o preço vigente do anúncio como preço inicial de catálogo e custo interno zero, sinalizando que o custo precisa ser revisado no admin.
- Todo produto importado é vinculado aos catálogos ativos do plano do cliente selecionado.
- Cada item/variação do Mercado Livre recebe um `TenantMarketplaceListingMap`, inclusive anúncios espelhados/sincronizados, para garantir o reconhecimento dos pedidos.
- A operação é idempotente e gera o evento auditável `AdminProducts.ImportFromMercadoLivre`.

## Interface e endpoint

- Admin > cliente > integração Mercado Livre > **Buscar produtos do ML** abre uma prévia. O admin filtra, seleciona produtos agrupados por SKU e importa somente os escolhidos.
- `POST /api/v1/admin/tenants/{tenantSlug}/clients/{clientId}/integrations/mercadolivre/catalog/import`.
- O request aceita busca, marcas, estoque e modo de prévia; a interface atual envia a busca vazia, Boca Rosa/Principia e estoque 1.000.
- `ItemIds` limita a gravação aos anúncios selecionados. A prévia pode consultar tudo, mas nenhuma gravação acontece antes da seleção explícita.

## Inteligência de seller

- O mesmo painel permite consultar anúncios públicos de outro vendedor pelo Seller ID e termo opcional.
- A consulta usa `/sites/MLB/search?seller_id=...` e exibe título, marca, preço e item ID.
- É uma operação somente leitura; não acessa dados privados, pedidos, estoque real ou credenciais do seller pesquisado.

## Observação operacional

A importação cria o catálogo e os mappings. Uma sincronização de pedidos posterior reaplica o reconhecimento por SKU aos pedidos do período processado.
