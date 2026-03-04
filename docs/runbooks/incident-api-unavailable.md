# Runbook: Incident - API Unavailable

## Triage order
1. ALB target health for `sabr-api-<env>`.
2. ECS service events and failing tasks.
3. CloudWatch logs (`/aws/ecs/sabr/api/<env>`).
4. RDS availability and connections.

## Fast actions
1. If only latest deployment failed: rollback task definition.
2. If RDS exhausted connections: scale ECS down temporarily and restart failing workers.
3. If high 5xx with healthy tasks: check app config secret (`<project>/<env>/api-config`).

## Evidence to collect
1. Time window (UTC).
2. Task IDs failing.
3. Last deployment SHA.
4. Error signature from logs.

## Resolution gate
Only close incident after:
1. Health checks stable for 15 minutes.
2. Error rate back to baseline.
3. Root cause and corrective action documented.
