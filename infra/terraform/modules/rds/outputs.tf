output "db_identifiers" {
  value = { for env, db in aws_db_instance.this : env => db.id }
}

output "db_endpoints" {
  value = { for env, db in aws_db_instance.this : env => db.address }
}

output "db_arns" {
  value = { for env, db in aws_db_instance.this : env => db.arn }
}

output "app_secret_arns" {
  value = { for env, secret in aws_secretsmanager_secret.app : env => secret.arn }
}
