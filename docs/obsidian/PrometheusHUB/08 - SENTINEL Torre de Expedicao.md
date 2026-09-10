---
tags: [prometheushub, sentinel, expedicao, sla, mercadolivre]
updated: 2026-09-10
---

# SENTINEL — Torre de Expedição

O SENTINEL é a leitura analítica da operação. A Expedição continua responsável pelas ações físicas; a torre deriva risco sem modificar fatos internos ou externos.

## Invariantes

- `MarketplaceShipmentOperationalState` contém somente fatos produzidos pelo PrometheusHUB: impressão, início de separação, separado e embalado, com responsáveis.
- `MarketplaceShipmentExternalState` contém somente fatos confirmados pelo provider. A ação administrativa nunca cria `shippedAt`.
- O estado apresentado pelo SENTINEL é derivado das duas projeções e não é persistido como verdade substituta.
- A bipagem final registra `packed`; “enviado” depende exclusivamente do marketplace.
- `date_handling` não significa envio e `date_first_printed` não significa deadline.
- Dados antigos não sobrescrevem versão externa mais nova.

## Deadline oficial

`MarketplaceShipmentDispatchDeadlineVersion` é append-only. Cada alteração oficial recebida de `/shipments/{id}/sla` grava deadline, URL-fonte, horário da consulta, última atualização do provider, hash canônico e versão monotônica. Payload com o mesmo hash não cria versão.

O campo compatível `MarketplaceShipment.ShipByDeadlineAt` recebe somente a versão oficial atual. O backfill não converte deadlines antigos não verificáveis em fatos oficiais.

## Sincronização e frescor

Webhook é primário. O reconciliador persistente agenda uma leitura de proteção a cada cinco minutos, ou um minuto para pacote embalado, com lease compare-and-set. O lote global e o lote por seller limitam pressão no provider; o cliente HTTP aplica circuit breaker e backoff com jitter.

Frescor:

- `FRESH`: dentro da janela esperada;
- `STALE`: mais de 5 minutos para críticos/embalados ou 15 minutos para os demais abertos;
- `INTEGRATION_RISK`: erros repetidos ou falha conhecida.

`STALE` e `INTEGRATION_RISK` se sobrepõem visualmente ao risco de SLA sem apagar o diagnóstico operacional.

## Eventos

`sentinel.sla.level_changed` usa deduplicação por provider, seller, shipment, versão do deadline e nível. O payload inclui escopo, pedido/shipment, deadline e hash, horário de avaliação, nível anterior/novo, minutos restantes, estados interno/externo, causa, frescor, última sincronização e `dedupKey`. Está pronto para consumo futuro pelo n8n; WhatsApp permanece desativado.

## Segurança e operação

- `Analyst/Analista` acessa somente `/sentinel` e as APIs GET correspondentes.
- Admin e SuperAdmin também consultam; somente SuperAdmin altera a política de risco.
- Finance e clientes não acessam.
- O feature flag do frontend é `sentinelV1`.
- Migração `AddSentinelFulfillmentTower` faz backfill conservador: eventos legados `processed/dispatched` podem reconstruir `packedAt`, mas nunca `shippedAt`.

## Recuperação

Em indisponibilidade do Mercado Livre, manter a operação interna e observar `INTEGRATION_RISK`. Não corrigir `shippedAt` manualmente. Após recuperação, liberar leases vencidos naturalmente e deixar a reconciliação reconsultar o provider. Divergências de deadline permanecem auditáveis pelo histórico de versões.
