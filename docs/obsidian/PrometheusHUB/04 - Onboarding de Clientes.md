---
tags: [prometheushub, clientes, onboarding, senha, cadastro]
updated: 2026-09-29
---

# Onboarding de clientes

## Fluxo

- O reset administrativo altera apenas a senha e `MustChangePassword`; nunca rebaixa o `Status` do cliente.
- Depois da troca obrigatória, cliente aprovado segue diretamente ao dashboard.
- Cliente com cadastro incompleto continua pelas etapas Empresa, Contato/Endereço, Responsável e Documentos.
- O status retornado por `GET /client/profile` é a fonte atualizada usada pelo frontend antes de decidir o destino.
- “Salvar depois / Voltar ao login” executa logout real antes da navegação; apenas trocar a rota mantinha a sessão e o guard devolvia o cliente ao onboarding.
- Datas de nascimento recebidas sem fuso são tratadas como datas civis e persistidas à meia-noite UTC, sem deslocar o dia. Isso evita erro do Npgsql ao gravar o `timestamptz` legado.
- Ao solicitar análise de vários documentos em paralelo, cada documento é persistido antes da verificação do conjunto obrigatório. A transição de `PendingDocuments` para `UnderReview` é feita por atualização condicional no banco para não deixar o cliente preso como pendente.

## Inscrição estadual

A IE é obrigatória para pessoa jurídica não isenta. O checksum da biblioteca `Brazil.Data` continua sendo a regra geral. Para SP, uma IE estruturalmente válida com 12 dígitos também é aceita quando a tabela da biblioteca ainda não reconhece o número; sequências repetidas continuam rejeitadas.

A confirmação documental e administrativa permanece como fonte de verdade para aprovação do cadastro. Essa decisão evita que divergências da biblioteca impeçam o cliente de concluir o onboarding.

## Regressão coberta

- IE paulista `155.203.127.118`, vinculada a cadastro ativo, deve ser aceita.
- IE ausente, curta ou composta por um único dígito repetido deve ser rejeitada.
- Data de nascimento com `DateTimeKind.Unspecified` deve manter o dia informado e ser gravada como UTC.
- O envio paralelo dos quatro documentos obrigatórios deve concluir com o cliente em `UnderReview`.
