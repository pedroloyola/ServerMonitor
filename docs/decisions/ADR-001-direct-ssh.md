# ADR-001 — Ligação direta por SSH

Estado: **aceite; fundação de ligação implementada no Milestone 3**.

O MVP liga diretamente do cliente Windows aos servidores Ubuntu/macOS por SSH, sem backend, Prometheus ou Grafana. O Milestone 3 implementa autenticação segura, validação de host key, teste explícito e deteção do sistema operativo. Métricas e monitorização periódica continuam reservadas para milestones posteriores.
