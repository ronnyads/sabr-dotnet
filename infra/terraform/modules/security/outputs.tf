output "alb_security_group_ids" {
  value = { for env, sg in aws_security_group.alb : env => sg.id }
}

output "ecs_security_group_ids" {
  value = { for env, sg in aws_security_group.ecs : env => sg.id }
}

output "rds_security_group_ids" {
  value = { for env, sg in aws_security_group.rds : env => sg.id }
}
