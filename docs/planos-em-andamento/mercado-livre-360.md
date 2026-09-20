# PrometheusHUB — Mercado Livre 360

Data: 20/09/2026. Plano aprovado, em implementação incremental.

## Progresso verificado em 20/09/2026

- O admin separa produtos internos de registros `MLB...` legados e oferece vínculo direto a uma variante/SKU existente, com revisão explícita. O fluxo não cria outro produto nem altera estoque.
- Um novo produto simples agora cria a variante padrão com o mesmo SKU, saldo físico zero e buffer de segurança 2 na mesma transação. Ao ativar um produto sem catálogo específico, o backend o associa ao Catálogo Público; catálogos explicitamente escolhidos não são substituídos.
- A API valida tenant, cliente, integração, seller da credencial e proprietário do anúncio remoto, catálogo autorizado, variação e versão otimista. O remapeamento é auditado e só afeta pedidos futuros ou itens ainda não resolvidos; snapshots já resolvidos permanecem intactos.
- Correções de webhook preso, lease de worker, job antigo de estoque após remapeamento, escolha explícita na importação e divergência/moeda financeira foram implementadas em commits anteriores da mesma onda.
- Validação local: 148 testes não-integrados, 44 testes de integração Mercado Livre e 18 testes direcionados de produtos/vínculo aprovados; builds de produção de admin e cliente aprovadas. A suíte completa de integração ainda apresenta falhas em outros módulos e não é critério verde desta onda. Não há homologação real do fluxo novo com o seller piloto.
- Billing conciliatório, claims/devoluções, unidades pré-compradas do seller e analytics diário continuam pendentes. Nenhum deles deve ser exibido como concluído ou lucro confirmado.
- Na continuação de 20/09/2026, corrigido o checkout de pedidos Mercado Livre ainda não pagos no canal: bloqueio antes do débito e separação de `PaidAt` externo de `SabrPaymentConfirmedAt` interno. Corrigida também a validação do tenant na listagem da rota legada de catálogos e o retorno degradado de sugestão de categoria com token já cancelado. Os testes legados foram atualizados ao contrato global de planos/catálogos, ao auto-mapping persistente e à contagem de unidades; a suíte completa passou, 359/359. Builds de admin/cliente passaram. Publicado como recorte de segurança: Fly v162 com código `bc78d4f`, depois v163 test-only `12471fb`; API e worker iniciados e health HTTP 200. Novos bundles de admin/cliente servidos nos domínios de produção. A regra ainda não foi homologada com pedido real do seller.
- Próximo recorte de 20/09/2026: verificação real e somente de leitura da capacidade Billing ML por seller, em vez de inferi-la apenas de OAuth. Erros transitórios preservam a última verificação, mas ficam visíveis como risco. Este probe não implementa o consumidor de Billing, não gera lançamentos conciliados e não conclui a rentabilidade. Testes: 9 dirigidos de probe/capacidade e suíte completa 368/368 aprovados. Deploy e verificação em produção serão registrados separadamente.
- O recorte de Billing foi publicado como Fly v164 (`c9cdee8`), workflow verde, API e worker iniciados, health HTTP 200. Ainda falta acionar o probe autenticado na conta piloto e verificar o resultado real de autorização.
- O webhook `orders_v2` agora sincroniza o pedido específico, sem varredura da janela inteira do seller; shipment/payment ainda usam a reconciliação ampla até haver identidade de pedido inequívoca. O teste de integração dirigido passou com a busca ampla propositalmente falhando. Suíte e deploy desse segundo recorte permanecem pendentes nesta anotação.

## Orientação para quem implementar

Leia os `AGENTS.md` aplicáveis e `docs/obsidian/PrometheusHUB/00 - Mapa do Sistema.md`. Confirme os achados abaixo no checkout atual antes de editar. Este documento registra planejamento e auditoria; não significa que as funcionalidades estejam concluídas ou homologadas. Não reescreva recursos existentes sem auditá-los. Implemente por etapas verificáveis, registre evidências e atualize a documentação permanente e o Obsidian. Não registre credenciais.

Repositórios:

- Backend: `C:/Users/euron/Documents/Projetos Liberty IA/PrometheusHUB/sabr-dotnet`.
- Frontend: `C:/Users/euron/Documents/Projetos Liberty IA/PrometheusHUB/sabr-frontend`.

## 1. Objetivo e resultado da auditoria

Concluir a integração existente, corrigindo os fluxos incompletos e preservando os dados históricos. O sistema deve conectar catálogo, anúncios, pedidos, pagamento ao Prometheus, estoque, expedição, devoluções e rentabilidade.

A auditoria foi dividida entre três agentes, com GSD para planejamento, FORGE para catálogo e referências de PostgreSQL e UI/UX. Os resultados abaixo são evidências do código; publicação real, concorrência no PostgreSQL e conciliação financeira ainda precisam de homologação.

| Capacidade | Implementação atual | Situação e ação |
|---|---|---|
| Catálogo e SKU | `Product`, `ProductVariant`, catálogos públicos/restritos | Existe. Produto simples pode nascer sem variante vinculável; unificar criação. |
| Cadastro administrativo | “Novo Produto” e “Criar SKU interno” executam a mesma ação | Corrigir redundância e remover “Criar meu SKU” das linhas legadas. |
| Importação ML | Importador permite criar produto e remapear anúncio | Parcial. Separar clonagem de vínculo; impedir estoque padrão ao vincular. |
| Vínculo | Há caminhos distintos de mapeamento | Consolidar validação, autorização, versão e auditoria. |
| Publicação | Wizard de drafts, atributos, validação e persistência do vínculo | Reaproveitar. Testes usam API simulada; falta homologação real e completar User Products. |
| Edição de anúncios | Adapters e Capability Engine | Reaproveitar e completar execução persistente, preço e capacidades por arquitetura. |
| Webhooks | Inbox e deduplicação existentes | Corrigir eventos presos em processamento e processamento por recurso. |
| Reconciliação | Jobs financeiros com checkpoint e rotina noturna | Completar reconciliação operacional frequente, leases e finalização condicionada ao dono da execução. |
| Pedidos | Importação, itens e snapshot de mapping | Corrigir regressão por resposta antiga e separar snapshot comercial de estado corrente. |
| Estoque | Reserva, buffer, versão e jobs | Corrigir elegibilidade, expiração e jobs que sobrevivem a remapeamento. |
| Pagamento interno | Checkout com carteira | Corrigir consultas PostgreSQL, custo congelado e ligação com ledger. Acrescentar uso de unidades pré-compradas. |
| Expedição/SENTINEL | Estados internos/externos e acompanhamento implementados | Auditar caminhos legados e homologar transições e deadlines. |
| Ledger | Entradas, cabeças, maturidade e projeções | Reaproveitar; corrigir precedência, moedas, divergências e confirmação do custo. |
| Billing | OAuth/probes e estruturas de cursor | Consumidor conciliatório ainda ausente. |
| Claims/devoluções | Sem ciclo completo identificado | Implementar coleta, inspeção, restituição e recuperação de custo. |
| Analytics | Dashboard financeiro existente | Faltam projeção diária, filtros completos e detalhamento explicável. |

As notas atuais que descrevem recursos ainda incompletos serão corrigidas junto às entregas.

## 2. Domínio e regras definitivas

### Produto, anúncio e histórico

```text
Produto interno → Variante/SKU → Produto do cliente
                                  ↓
                           Anúncio do seller
                                  ↓
                      Vínculo versionado ao SKU
                                  ↓
Pedido marketplace → Item com snapshot → Compra interna do seller
                                             ↓
                                  Carteira ou unidades pré-compradas
                                             ↓
                                      Expedição e financeiro
```

- O produto nasce uma única vez no catálogo Prometheus.
- Produto simples terá uma variante com o mesmo SKU, criada na mesma transação.
- Cada variação comercial terá SKU próprio.
- Anúncios sincronizados entram no inventário de anúncios do seller, mesmo sem vínculo. Sincronizá-los não cria produtos internos.
- Auto-mapping exige SKU externo não vazio e exatamente uma variante ativa e autorizada correspondente.
- Nome, imagem e EAN podem ajudar na revisão, mas não autorizam vínculo automático.
- Remapeamento altera pedidos futuros. Itens ainda sem resolução poderão receber sua primeira resolução auditada; snapshots já resolvidos permanecem preservados.
- Cadastros `MLB...` ficam em **Legado**, conforme escolha do usuário. Sua retirada do catálogo principal exige verificar vínculos ativos; registros e pedidos históricos permanecem consultáveis.

### Reserva comprada e reserva operacional

A explicação mais recente do usuário prevalece sobre a ambiguidade do documento: **a reserva comprada pertence ao seller**. Ela será modelada separadamente do bloqueio temporário de estoque para um pedido.

Registro da orientação do usuário:

> A reserva é dele ele ja comprou essa reserva de nós, só desconta a reserva se ele pagar pelo pedido, alerta ele que ele tem pedidos ativos para pagar! ele pode pagar com a sua reserva ou com seu saldo na carteira.

- **Unidades pré-compradas:** saldo por cliente e SKU, com lotes de aquisição e custo histórico.
- **Reserva operacional:** bloqueio de unidades para um pedido, sem consumir o saldo pré-comprado nem debitar carteira.
- Venda paga no ML gera pedido pendente de pagamento interno e tentativa de bloqueio operacional.
- Ao pagar no Prometheus, o seller escolhe **usar unidades pré-compradas** ou **pagar com carteira**.
- Usar unidades pré-compradas consome o direito já adquirido, sem nova cobrança pelo produto.
- Pagar com carteira debita o preço congelado do pedido.
- Pagamento combinado será permitido: unidades disponíveis da reserva comprada e restante na carteira, com composição explícita na revisão.
- Unidades compradas não expiram porque o seller deixou um pedido pendente. O sistema alerta sobre pedidos ativos aguardando pagamento.
- O bloqueio operacional de uma venda ativa não será liberado automaticamente por um timer. Cancelamento confirmado ou resolução administrativa auditada encerra esse bloqueio.
- Sem SKU ou estoque suficiente, o pedido fica bloqueado e gera pendência operacional.

Para impedir dupla contagem, o saldo físico distinguirá estoque de venda geral de estoque já pertencente aos clientes:

```text
Disponível geral = máximo(0, físico − unidades de clientes − bloqueios gerais − buffer)

Disponível do cliente = unidades pré-compradas ainda não bloqueadas/consumidas
```

Bloqueios sobre unidades pré-compradas não serão novamente descontados do disponível geral. O consumo físico terá um único marco transacional, compatível com o checkout existente; embalagem e envio não repetirão a baixa.

### Custo e pagamentos

- `MarketplacePayment` representa o pagamento do comprador no canal.
- `ClientPurchase` representa a obrigação e a liquidação do seller no Prometheus.
- Preço catálogo fica congelado na primeira resolução válida do item.
- Para unidades pré-compradas, o custo realizado vem do lote adquirido, consumido por ordem de aquisição.
- Ao usar carteira, o custo realizado vem do snapshot do pedido.
- O ledger registra estimativa e realização sem sobrescrever os fatos anteriores.
- Compra antecipada de unidades não gera custo de venda duplicado: o custo é atribuído ao pedido quando as unidades são consumidas.

## 3. Mudanças de implementação

### Catálogo, vínculo e publicação

**Admin**

- Manter um único botão **Novo produto**.
- Oferecer **Clonar dados de anúncio ML** como ação separada, com revisão e busca de duplicidades.
- Oferecer **Vincular ao SKU interno** diretamente no anúncio, mostrando imagem, título, seller, variação, vínculo atual e destino.
- Quando a identidade já identifica o cliente e seller, o backend resolve o contexto; não redirecionar para seleção genérica de cliente.
- Se houver múltiplas identidades, selecionar explicitamente a conta/variação dentro do fluxo.
- Confirmar e atualizar a tela com o vínculo persistido.
- Vincular não cria estoque nem altera preço. Variante ausente gera orientação para completar o produto.

**Portal**

- Catálogo: adicionar aos meus produtos, publicar ou vincular.
- Meus Produtos: produtos internos associados ao cliente, suas contas e anúncios.
- Anúncios: sincronizados, vinculados, pendentes e com erro.
- Publicação usa o wizard existente: preparar → validar → revisar → confirmar → acompanhar job.
- Descontinuar o caminho simplificado de publicação, mantendo compatibilidade através do serviço consolidado.

**Contratos**

- Identidade persistente de anúncio independente do mapping, incluindo provider, integração, seller, item, variação e User Product.
- Comandos separados para clonar produto, criar vínculo e remapear.
- Vínculo exige versão esperada; conflito retorna atualização necessária.
- Preflight retorna arquitetura, campos ausentes, bloqueios e avisos.
- Operações remotas retornam `202` e identificador de job.
- Adapter escolhe publicação, preço e estoque conforme capacidades reais da conta.

### Pedidos, filas, estoque e pagamento

- Inbox confirma recebimento somente após persistência.
- Worker processa o recurso notificado e usa lease recuperável, heartbeat, backoff, jitter e fila de falhas.
- Finalização exige que a tentativa ainda seja dona do lease.
- Reconciliação de pedidos abertos a cada cinco minutos; shipments críticos conforme a política SENTINEL.
- Respostas antigas não regridem estado. Correções oficiais mais novas geram versões/eventos.
- Corrigir o uso de arrays nas consultas de bloqueio PostgreSQL e testar com PostgreSQL real.
- Jobs de estoque validam `inventoryVersion`, versão do vínculo e SKU de destino antes de cada tentativa.
- Serializar escritas remotas pela identidade efetiva de estoque, inclusive User Product compartilhado por anúncios.
- Checkout bloqueia pedido, fonte de pagamento e inventário em ordem consistente; grava consumo, carteira, ledger e outbox na mesma transação.
- Repetição ou concorrência nunca cobra nem consome novamente.
- Cancelamento antes do pagamento libera bloqueios. Depois do pagamento, restituição depende do estágio operacional; produto já enviado segue o fluxo de devolução.

### Financeiro, devoluções e analytics

- Implementar consumidor Billing sobre jobs e cursores existentes, sequencial por seller/grupo/período.
- Tratar `206` como parcial e `429` como retentativa; falta de autorização mantém pendência.
- Proteger ledger contra alteração/exclusão; impedir estimativa antiga de substituir confirmação.
- Agregar apenas cabeças ativas e separar moedas.
- Calcular divergência apenas entre estimativa e confirmação correspondentes. Falta de confirmação não equivale a diferença negativa.
- Conectar o checkout efetivamente utilizado à confirmação financeira.
- Importar claims/returns com estado externo separado da inspeção interna.
- Recuperação de custo exige retorno vendável e restituição interna confirmada.
- Restituir à origem: carteira ou unidades pré-compradas, com prevenção de crédito duplicado.
- Preservar valores sem alocação oficial; rentabilidade por SKU permanece incompleta nesses casos.
- Criar projeção diária por data, cliente, seller, SKU, anúncio e moeda, com reprocessamento de períodos afetados por ajustes tardios.
- Expor receita, taxas, frete, custo, lucro, margem, devoluções, maturidade, cobertura e divergências com detalhamento até a origem.

### Estrutura aditiva de dados

Reaproveitar entidades atuais e acrescentar:

| Estrutura | Finalidade |
|---|---|
| Anúncio persistente e versões de vínculo | Representar anúncios sem SKU e manter histórico dos remapeamentos |
| `ClientPurchase` e alocações de pagamento | Separar compra interna da venda no marketplace |
| Lotes e movimentos de unidades pré-compradas | Registrar propriedade, custo, bloqueio, consumo e restituição |
| Claims, returns e inspeções | Separar fatos do canal da decisão interna sobre a mercadoria |
| Projeção diária financeira/comercial | Consultas eficientes por produto, anúncio e seller |

Tenant, cliente, seller, moeda e identidade externa serão dimensões explícitas onde aplicáveis. Migrações não converterão automaticamente reservas operacionais atuais em unidades pertencentes a clientes.

## 4. APIs externas, estados e experiência

### Matriz de integração

| Domínio | Recursos a validar e utilizar |
|---|---|
| Conta | OAuth, `/users/me`, capacidades e aplicações ML/MP separadas |
| Anúncios | Busca de itens do seller, `/items`, categorias, atributos, imagens, descrição, preços e User Products |
| Estoque | Legacy `/items`; User Products `/user-products/{id}/stock` e escrita por localização com `x-version` |
| Pedidos | `orders_v2`, `/orders`, descontos, packs e associação atual de shipments |
| Expedição | `/shipments/{id}`, SLA, histórico, atrasos, lead time e etiquetas |
| Financeiro | Billing por período/grupo ML e MP; recursos MP autorizados e correlacionados |
| Pós-venda | Claims, returns por claim, custos de retorno e revisões |

A escolha exata de endpoints e headers será registrada na auditoria de cada adapter. Conferir a documentação oficial atual antes de implementar cada domínio, evitando recursos descontinuados:

- [User Products](https://developers.mercadolivre.com.br/pt_br/user-products)
- [Estoque multiorigem](https://developers.mercadolivre.com.br/pt_br/estoque-multi-origem)
- [Estoque distribuído](https://developers.mercadolivre.com.br/pt_br/estoque-distribuido)
- [Orders](https://developers.mercadolivre.com.br/pt_br/gerenciamento-de-vendas)
- [Envios](https://developers.mercadolivre.com.br/pt_br/gerenciamento-de-envios)
- [Billing — boas práticas](https://developers.mercadolivre.com.br/pt_br/boas-praticas-para-o-consumo-das-apis-de-relatorios-de-faturamento)
- [Devoluções](https://developers.mercadolivre.com.br/pt_br/gerenciar-devolucoes)
- [Reclamações](https://developers.mercadolivre.com.br/pt_br/gerenciar-reclamacoes)

### Estados independentes

- **Anúncio:** sincronizado sem vínculo → vinculado; publicação: rascunho → validado → enfileirado → publicado/erro.
- **Venda externa:** estado confirmado pelo marketplace, com versão e horário de atualização.
- **Compra interna:** bloqueada → aguardando pagamento → paga → restituição pendente → restituída parcial/total.
- **Bloqueio de unidades:** ativo → consumido/liberado.
- **Fulfillment interno:** etiqueta → separação → separado → embalado.
- **Envio externo:** aguardando coleta → enviado confirmado → entregue/devolvido/cancelado.
- **Financeiro:** incompleto → estimado → parcialmente confirmado → confirmado → reaberto.

### Interface

Manter identidade visual do PrometheusHUB, Outfit/Inter e tokens light/dark. A prioridade visual será clareza:

- Separar saldo da carteira, unidades pré-compradas e unidades bloqueadas em pedidos.
- Mostrar pendências com ação direta para resolver.
- Exibir saúde por recurso, última sincronização e erros; “conectado” não significa “saudável”.
- Concentrar Mercado Livre em uma área com visão geral, anúncios, pedidos, rentabilidade, devoluções e integração.
- Preservar rotas atuais por redirecionamento.
- Nenhum número fictício ou valor incompleto apresentado como lucro confirmado.

## 5. Implantação e critérios de conclusão

### Ordem de execução

1. **Auditoria final e baseline:** matriz por capacidade, evidências, versões implantadas e homologação disponível.
2. **Correções críticas:** vínculo direto, produto simples com variante, consultas PostgreSQL, leases, regressão de eventos e proteção de jobs após remapeamento.
3. **Conexão e anúncios:** sincronização persistente, publicação unificada, Legacy/User Products e capacidades.
4. **Pedidos e compra interna:** snapshots, unidades pré-compradas, bloqueios, carteira e pagamento combinado.
5. **Expedição/SENTINEL:** transições, etiquetas, alertas de pagamento e confirmação externa.
6. **Financeiro e devoluções:** ledger corrigido, Billing/MP, inspeção e restituições.
7. **Analytics e experiência:** projeções diárias, páginas por SKU/seller e detalhamento financeiro.

Cada etapa terá testes e liberação própria. O escopo fica consolidado neste plano; falhas encontradas não serão escondidas sob novas telas.

### Migração

- Migrações aditivas e flags por capacidade/seller.
- Backfills particionados, com checkpoint e relatório de divergências.
- Histórico comercial, financeiro e de mappings permanece preservado.
- Produtos legados saem da lista principal após tratamento dos vínculos.
- Saldo de unidades pré-compradas só será migrado com evidência de aquisição e quantidade; ausência gera pendência.
- Primeira escrita remota de estoque ocorre após comparação em modo observação.

### Testes obrigatórios

- Vincular o anúncio correto à variação correta, sem criar outro produto ou estoque.
- Validar o caso `MLB7587037788`/`PH-PAYOT-02` com identidade e dados reais antes de gravar.
- Publicação simples e com variações em conta de teste habilitada, comprovando anúncio remoto e vínculo local.
- SKU duplicado, inexistente ou não autorizado permanece pendente.
- Dois sellers disputando o último saldo sem ultrapassar disponibilidade.
- Pagamento com carteira, unidades compradas e composição dos dois.
- Cancelamento antes/depois do pagamento e devolução vendável/avariada.
- Webhook duplicado, atrasado, perdido e worker interrompido.
- Job antigo após mudança de estoque ou remapeamento.
- Testes de concorrência e transações em PostgreSQL real.
- Billing parcial, rate limit, ajuste tardio, moeda distinta e refund sem alocação.
- Reconciliação manual de amostra representativa, com causa para toda divergência de centavos.
- Teclado, responsividade, contraste, dark mode e feedback de progresso.

**Conclusão:** uma capacidade só será marcada como funcionando após evidência proporcional: código, teste, homologação e verificação após deploy. O salvamento deste plano não executa migrações, publicações nem alterações de produção.

## Apêndice — evidências técnicas para retomar a auditoria

Achados comunicados pelos agentes, a reconfirmar no checkout antes da alteração:

- `MercadoLivreCatalogImportService.ImportAsync`: Product existente sem ProductVariant pode receber variante com `PhysicalStock=request.PhysicalStock`; UI usa padrão 1000. `ItemIds` vazio pode selecionar todos os anúncios. Separar contrato de vínculo estrito.
- `AdminProducts`: `openCreate()` atende dois botões; `createInternalSkuFrom()` apenas preenche formulário; salvar produto não cria mapping. `openMarketplaceLink()` apenas navega para integração do cliente.
- `MercadoLivreMappingService.CreateAsync`: caminho antigo verifica conexão e variante, mas faltam validação do anúncio remoto, atividade/autorização do produto, versão e auditoria consistentes. Consolidar com fluxo normalizado.
- `ListingDraftService` e `ClientListingDraftsController`: caminho mais completo de publicação, com preflight, atributos e variações. `MercadoLivrePublishService` mantém caminho simplificado concorrente. Testes `ListingDraftHttpTests` usam `FakeMercadoLivreApiClient`.
- `StockAvailabilityService.ProcessStockJobAsync`: conferir a ausência de comparação entre SKU/versão atual do mapping e payload do job; apenas inventoryVersion não protege contra remapeamento.
- `MarketplaceOrderInventoryService.ReconcileReservationsCoreAsync`: conferir reserva de pedidos não terminais sem exigir pagamento ML. `ExpireReservationsAsync`: conferir locks/transação e liberação de bloqueios para vendas ainda ativas.
- `MarketplaceOrderCheckoutService`: conferir falta de requisito ML paid, congelamento do preço somente no checkout e `LoadVariantsAsync` com `ANY ({0})`/`string[]`, padrão associado ao erro PostgreSQL 42809 em nota operacional.
- `MercadoLivreSyncService.UpsertOrderAsync`: conferir atualização de status, datas, quantidade e preços sem proteção por versão externa.
- `MercadoLivreWebhookService`: inbox existente, mas processamento pode ficar preso após crash; evento dispara sincronização por janela inteira em vez de recurso pontual.
- `FinancialSyncJobService`: `SKIP LOCKED`, lease e checkpoint existentes; conferir finalização sem compare-and-set do dono e renovação do lease. Processador identificado apenas para `OPERATIONAL_SYNC_CHUNK`.
- `FinancialEconomicHead` e `FinancialLedgerService`: conferir precedência de confirmação sobre estimativa antiga, proteção append-only e moeda nos agregados.
- `FinancialProfitabilityService`: conferir soma entre moedas e divergência global que inclui estimativas sem confirmação correspondente.
- `ConfirmProductCostsAsync`: identificado no serviço de pagamento antigo; conferir ligação ausente no checkout utilizado pelos controllers.
- Billing: entidades e probes existentes não equivalem a consumidor conciliatório implementado. Claims/returns/inspeção e projeção diária não foram identificados como fluxos completos.
- Auditoria financeira reportou 8 testes direcionados aprovados, porém com EF InMemory/HTTP stub. Não considerar isso prova de concorrência PostgreSQL ou acesso real Billing.

### Materiais de origem

- Ideia completa do usuário: `C:/Users/euron/.codex/attachments/14cb9ca3-f796-4620-a30d-016af31b9f18/Texto colado.txt`.
- Plano enviado pelo usuário: `C:/Users/euron/.codex/attachments/8075db20-1cc9-4fc5-998b-6b2f63d107a9/Texto colado.txt`.
- Notas permanentes: `docs/obsidian/PrometheusHUB`.
- Cofre compartilhado: `C:/Users/euron/Documents/Obsidian`.

Os anexos são referências locais complementares. Este plano contém o handoff consolidado; instruções explícitas posteriores do usuário prevalecem.
