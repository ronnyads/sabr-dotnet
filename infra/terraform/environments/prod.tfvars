project_name = "sabr"
aws_region   = "sa-east-1"
root_domain  = "example.com.br"

nat_gateway_mode = "single"

db_instance_class_prod = "db.t4g.micro"

ecs_desired_count_prod = 1
ecs_min_capacity_prod  = 1
ecs_max_capacity_prod  = 4

github_backend_repository  = "your-org/sabr-dotnet"
github_frontend_repository = "your-org/sabr-frontend"
github_infra_repository    = "your-org/sabr-dotnet"
