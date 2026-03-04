output "repository_urls" {
  value = { for env, repo in aws_ecr_repository.api : env => repo.repository_url }
}

output "repository_arns" {
  value = { for env, repo in aws_ecr_repository.api : env => repo.arn }
}

output "repository_names" {
  value = { for env, repo in aws_ecr_repository.api : env => repo.name }
}
