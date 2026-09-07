---
tags: [prometheushub, clientes, onboarding, senha, cadastro]
updated: 2026-09-07
---

# Onboarding de clientes

## Fluxo

- O reset administrativo altera apenas a senha e `MustChangePassword`; nunca rebaixa o `Status` do cliente.
- Depois da troca obrigatória, cliente aprovado segue diretamente ao dashboard.
- Cliente com cadastro incompleto continua pelas etapas Empresa, Contato/Endereço, Responsável e Documentos.
- O status retornado por `GET /client/profile` é a fonte atualizada usada pelo frontend antes de decidir o destino.

## Inscrição estadual

A IE é obrigatória para pessoa jurídica não isenta. O checksum da biblioteca `Brazil.Data` continua sendo a regra geral. Para SP, uma IE estruturalmente válida com 12 dígitos também é aceita quando a tabela da biblioteca ainda não reconhece o número; sequências repetidas continuam rejeitadas.

A confirmação documental e administrativa permanece como fonte de verdade para aprovação do cadastro. Essa decisão evita que divergências da biblioteca impeçam o cliente de concluir o onboarding.

## Regressão coberta

- IE paulista `155.203.127.118`, vinculada a cadastro ativo, deve ser aceita.
- IE ausente, curta ou composta por um único dígito repetido deve ser rejeitada.
