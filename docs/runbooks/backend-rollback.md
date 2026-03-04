# Runbook: Backend Rollback (ECS)

## Objective
Rollback API deployment to previous stable task definition in less than 15 minutes.

## Steps
1. Open ECS service in AWS Console (`sabr-api-dev` or `sabr-api-prod`).
2. Identify last healthy deployment task definition revision.
3. Update service and pin previous task definition revision.
4. Wait for service stability (`services-stable`).
5. Validate:
   - `/health/live`
   - `/health/ready`
   - critical login endpoint.

## CLI fallback
```bash
aws ecs update-service \
  --cluster <cluster-name> \
  --service <service-name> \
  --task-definition <task-family:revision>

aws ecs wait services-stable \
  --cluster <cluster-name> \
  --services <service-name>
```

## Post-rollback
1. Register incident note with root cause hypothesis.
2. Freeze merges to `main` until corrective patch exists.
3. Add regression test before re-release.
