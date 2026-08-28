# Política de Segurança — ServerAlyzer

## Versões suportadas

Enquanto a ServerAlyzer está a preparar a primeira versão pública estável, as correções de segurança
aplicam-se à **última versão** publicada (e ao ramo principal de desenvolvimento). Não são mantidas
versões antigas em paralelo nesta fase.

| Versão | Suportada |
|---|---|
| Última release / `main` | ✅ |
| Pré-releases anteriores | ❌ |

## Reportar uma vulnerabilidade

Reporta de forma **responsável e privada**, sem abrir um issue público com detalhes exploráveis:

- Preferencialmente através de **GitHub Security Advisories** (separador *Security* → *Report a
  vulnerability*) do repositório.
- Inclui: descrição, impacto, passos de reprodução e, se possível, versão afetada e ambiente.

Pedimos um período razoável para investigar e corrigir antes de divulgação pública (divulgação
coordenada). Damos crédito a quem reportar, se assim o desejar.

## O que **não** fazer

- Não publiques segredos, credenciais, chaves privadas ou dados pessoais num relatório ou issue.
- Não testes contra servidores/infraestrutura de terceiros sem autorização.

## Âmbito e modelo

A ServerAlyzer é local-first: sem backend, sem conta, sem telemetria. As áreas de segurança mais
relevantes são a fronteira de confiança SSH (host keys, sem auto-trust, mismatch bloqueia), o
armazenamento de segredos no Windows Credential Manager, o catálogo de comandos remoto **fechado e
só-leitura**, e a sanitização de texto remoto não confiável. Ver [`docs/architecture.md`](docs/architecture.md)
e os ADRs em [`docs/decisions/`](docs/decisions/).
