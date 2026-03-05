output "cluster_name" {
  value = aws_ecs_cluster.this.name
}

output "cluster_arn" {
  value = aws_ecs_cluster.this.arn
}

output "service_names" {
  value = { for env, svc in aws_ecs_service.api : env => svc.name }
}

output "service_arns" {
  # aws_ecs_service does not expose arn in all provider versions; id is stable.
  value = { for env, svc in aws_ecs_service.api : env => svc.id }
}

output "task_definition_families" {
  value = { for env, task in aws_ecs_task_definition.api : env => task.family }
}

output "alb_dns_names" {
  value = { for env, alb in aws_lb.api : env => alb.dns_name }
}

output "alb_zone_ids" {
  value = { for env, alb in aws_lb.api : env => alb.zone_id }
}

output "alb_arn_suffixes" {
  value = { for env, alb in aws_lb.api : env => alb.arn_suffix }
}

output "task_execution_role_arn" {
  value = aws_iam_role.task_execution.arn
}

output "task_role_arn" {
  value = aws_iam_role.task.arn
}
