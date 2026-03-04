locals {
  envs = ["dev", "prod"]
}

resource "aws_security_group" "alb" {
  for_each = toset(local.envs)

  name        = "${var.project_name}-alb-${each.key}-sg"
  description = "ALB security group for ${each.key}"
  vpc_id      = var.vpc_id

  ingress {
    description = "HTTP"
    from_port   = 80
    to_port     = 80
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  ingress {
    description = "HTTPS"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = merge(var.tags, {
    Name        = "${var.project_name}-alb-${each.key}-sg"
    Environment = each.key
  })
}

resource "aws_security_group" "ecs" {
  for_each = toset(local.envs)

  name        = "${var.project_name}-ecs-${each.key}-sg"
  description = "ECS security group for ${each.key}"
  vpc_id      = var.vpc_id

  ingress {
    description     = "App traffic from ALB"
    from_port       = 8080
    to_port         = 8080
    protocol        = "tcp"
    security_groups = [aws_security_group.alb[each.key].id]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = merge(var.tags, {
    Name        = "${var.project_name}-ecs-${each.key}-sg"
    Environment = each.key
  })
}

resource "aws_security_group" "rds" {
  for_each = toset(local.envs)

  name        = "${var.project_name}-rds-${each.key}-sg"
  description = "RDS security group for ${each.key}"
  vpc_id      = var.vpc_id

  ingress {
    description     = "PostgreSQL from ECS"
    from_port       = 5432
    to_port         = 5432
    protocol        = "tcp"
    security_groups = [aws_security_group.ecs[each.key].id]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = merge(var.tags, {
    Name        = "${var.project_name}-rds-${each.key}-sg"
    Environment = each.key
  })
}
