output "route53_zone_id" {
  value = module.dns_acm.hosted_zone_id
}

output "alb_certificate_arn" {
  value = module.dns_acm.alb_certificate_arn
}

output "cloudfront_certificate_arn" {
  value = module.dns_acm.cloudfront_certificate_arn
}

output "ecr_repository_names" {
  value = module.ecr.repository_names
}

output "ecr_repository_urls" {
  value = module.ecr.repository_urls
}

output "ecs_cluster_name" {
  value = module.ecs.cluster_name
}

output "ecs_service_names" {
  value = module.ecs.service_names
}

output "ecs_task_definition_families" {
  value = module.ecs.task_definition_families
}

output "s3_bucket_names" {
  value = module.s3_cloudfront.bucket_names
}

output "cloudfront_distribution_ids" {
  value = module.s3_cloudfront.distribution_ids
}

output "api_urls" {
  value = {
    dev  = "https://api-dev.${var.root_domain}"
    prod = "https://api.${var.root_domain}"
  }
}

output "frontend_urls" {
  value = {
    app_dev    = "https://app-dev.${var.root_domain}"
    admin_dev  = "https://admin-dev.${var.root_domain}"
    app_prod   = "https://app.${var.root_domain}"
    admin_prod = "https://admin.${var.root_domain}"
  }
}

output "app_secret_arns" {
  value = module.rds.app_secret_arns
}

output "github_backend_role_arn" {
  value = length(aws_iam_role.github_backend_deploy) > 0 ? aws_iam_role.github_backend_deploy[0].arn : null
}

output "github_frontend_role_arn" {
  value = length(aws_iam_role.github_frontend_deploy) > 0 ? aws_iam_role.github_frontend_deploy[0].arn : null
}

output "github_infra_role_arn" {
  value = length(aws_iam_role.github_infra_apply) > 0 ? aws_iam_role.github_infra_apply[0].arn : null
}
