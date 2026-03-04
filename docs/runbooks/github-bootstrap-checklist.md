# GitHub Bootstrap Checklist (Dev/Prod)

1. Create branches: `develop` and `main`.
2. Protect `main` with:
   - pull request required
   - status checks required
   - at least 1 approval
3. Create environments:
   - `development`
   - `production` (required reviewers enabled)
4. Add repo secrets:
   - `AWS_ROLE_ARN_DEV`
   - `AWS_ROLE_ARN_PROD`
5. Add repo variables (backend):
   - `AWS_REGION`
   - `ECS_CLUSTER_NAME`
   - `ECS_SERVICE_DEV`
   - `ECS_SERVICE_PROD`
   - `ECR_REPOSITORY_DEV`
   - `ECR_REPOSITORY_PROD`
6. Add repo variables (frontend):
   - `S3_BUCKET_CLIENT_DEV`
   - `S3_BUCKET_ADMIN_DEV`
   - `S3_BUCKET_CLIENT_PROD`
   - `S3_BUCKET_ADMIN_PROD`
   - `CF_DIST_CLIENT_DEV`
   - `CF_DIST_ADMIN_DEV`
   - `CF_DIST_CLIENT_PROD`
   - `CF_DIST_ADMIN_PROD`
   - `API_BASE_URL_DEV`
   - `API_BASE_URL_PROD`
