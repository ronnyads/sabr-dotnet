# Runbook: Cost Spike Incident (AWS)

## Detection
Trigger when daily spend deviates > 30% from baseline or billing alarm fires.

## Immediate checks
1. NAT Gateway data processing spike.
2. CloudFront transfer spike.
3. ECS scale-out anomaly.
4. RDS burst credits / storage growth.

## Containment actions
1. Temporarily cap ECS autoscaling max capacity.
2. Validate unexpected traffic sources (bots, abuse).
3. Enforce cache headers and CloudFront TTL policy.
4. Pause non-critical environments outside business hours if required.

## Root cause review
1. Identify exact service by Cost Explorer + CUR.
2. Tie spend increase to deployment/event timeline.
3. Implement permanent guardrail (WAF, autoscaling cap, budget alert, retention policy).

## Follow-up
- Update forecast and budget alarms.
- Publish postmortem with owner and due date for preventive action.
