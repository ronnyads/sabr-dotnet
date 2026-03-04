# SABR AWS Terraform Bootstrap

This stack provisions a full dev/prod baseline in AWS for `sabr-dotnet` + `sabr-frontend`.

## What this creates
- Shared VPC with public/private subnets and NAT (`single` or `per_az`).
- Security groups for ALB, ECS and RDS per environment.
- ECR repositories for API (`dev`/`prod`).
- RDS PostgreSQL instances (`dev`/`prod`) and Secrets Manager app secrets.
- ECS Fargate cluster/services (`sabr-api-dev`, `sabr-api-prod`) with autoscaling.
- S3 + CloudFront for frontend (`app-dev`, `admin-dev`, `app`, `admin`).
- Route53 records and ACM certificates (ALB in `sa-east-1`, CloudFront in `us-east-1`).
- GitHub OIDC roles for backend/frontend/infra pipelines.
- CloudWatch alarms and optional SNS email notifications.

## Prerequisites
- Terraform >= 1.7
- AWS credentials with admin access for bootstrap
- Public hosted zone in Route53 (or set `route53_zone_id`)

## Remote state (required before team usage)
Configure `backend "s3"` in your own `backend.tf` (not committed with account-specific values), for example:

```hcl
terraform {
  backend "s3" {
    bucket         = "<tf-state-bucket>"
    key            = "sabr/platform/terraform.tfstate"
    region         = "sa-east-1"
    dynamodb_table = "<tf-lock-table>"
    encrypt        = true
  }
}
```

## Bootstrap
```bash
cd infra/terraform
cp terraform.tfvars.example terraform.tfvars
# edit terraform.tfvars with your domain/repositories
terraform init
terraform plan -var-file=environments/dev.tfvars
terraform apply -var-file=environments/dev.tfvars
terraform apply -var-file=environments/prod.tfvars
```

## Key outputs to copy into GitHub
Run `terraform output` and register values in each repo (GitHub Variables/Secrets):
- `github_backend_role_arn`
- `github_frontend_role_arn`
- `ecr_repository_names`
- `ecs_cluster_name`
- `ecs_service_names`
- `s3_bucket_names`
- `cloudfront_distribution_ids`
- `api_urls`

## Cost profile (initial)
- NAT mode `single` for lower cost.
- `db.t4g.micro` for dev/prod.
- ECS desired count starts at 1 per env.

Upgrade path:
- Set `nat_gateway_mode = "per_az"`
- Increase RDS/ECS sizing
- Enable Multi-AZ on production RDS
