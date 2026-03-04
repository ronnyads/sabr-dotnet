locals {
  envs = {
    dev = {
      instance_class   = var.db_instance_class_dev
      backup_retention = var.db_backup_retention_dev
    }
    prod = {
      instance_class   = var.db_instance_class_prod
      backup_retention = var.db_backup_retention_prod
    }
  }
}

resource "aws_db_subnet_group" "this" {
  name       = "${var.project_name}-db-subnets"
  subnet_ids = var.private_subnet_ids

  tags = merge(var.tags, {
    Name = "${var.project_name}-db-subnets"
  })
}

resource "random_password" "db" {
  for_each = local.envs

  length           = 24
  special          = true
  override_special = "!#$%&*()-_=+[]{}<>:?"
}

resource "random_password" "jwt" {
  for_each = local.envs

  length  = 48
  special = false
}

resource "random_password" "ml" {
  for_each = local.envs

  length  = 36
  special = false
}

resource "aws_db_instance" "this" {
  for_each = local.envs

  identifier              = "${var.project_name}-${each.key}-postgres"
  engine                  = "postgres"
  engine_version          = "16.3"
  instance_class          = each.value.instance_class
  allocated_storage       = 20
  max_allocated_storage   = 100
  db_name                 = "${var.db_name_prefix}${each.key}"
  username                = var.db_username
  password                = random_password.db[each.key].result
  db_subnet_group_name    = aws_db_subnet_group.this.name
  vpc_security_group_ids  = [var.rds_security_group_ids[each.key]]
  publicly_accessible     = false
  storage_encrypted       = true
  backup_retention_period = each.value.backup_retention
  skip_final_snapshot     = each.key == "dev"
  deletion_protection     = each.key == "prod"
  apply_immediately       = each.key == "dev"
  multi_az                = false
  performance_insights_enabled = each.key == "prod"

  tags = merge(var.tags, {
    Name        = "${var.project_name}-${each.key}-postgres"
    Environment = each.key
  })
}

resource "aws_secretsmanager_secret" "app" {
  for_each = local.envs

  name                    = "${var.project_name}/${each.key}/api-config"
  recovery_window_in_days = 7

  tags = merge(var.tags, {
    Name        = "${var.project_name}-${each.key}-api-config"
    Environment = each.key
  })
}

resource "aws_secretsmanager_secret_version" "app" {
  for_each = local.envs

  secret_id = aws_secretsmanager_secret.app[each.key].id
  secret_string = jsonencode({
    ConnectionStrings__Default = "Host=${aws_db_instance.this[each.key].address};Port=5432;Database=${aws_db_instance.this[each.key].db_name};Username=${var.db_username};Password=${random_password.db[each.key].result};SSL Mode=Require;Trust Server Certificate=true"
    Jwt__Secret                = random_password.jwt[each.key].result
    Ml__ApiKey                 = random_password.ml[each.key].result
  })
}
