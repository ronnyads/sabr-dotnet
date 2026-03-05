locals {
  envs = {
    dev = {
      desired_count   = var.desired_count_dev
      min_capacity    = var.min_capacity_dev
      max_capacity    = var.max_capacity_dev
      cpu             = var.cpu_dev
      memory          = var.memory_dev
      app_environment = "Staging"
      allowed_origins = "https://app-dev.${var.root_domain},https://admin-dev.${var.root_domain}"
      api_subdomain   = "api-dev"
    }
    prod = {
      desired_count   = var.desired_count_prod
      min_capacity    = var.min_capacity_prod
      max_capacity    = var.max_capacity_prod
      cpu             = var.cpu_prod
      memory          = var.memory_prod
      app_environment = "Production"
      allowed_origins = "https://app.${var.root_domain},https://admin.${var.root_domain}"
      api_subdomain   = "api"
    }
  }
}

resource "aws_ecs_cluster" "this" {
  name = "${var.project_name}-cluster"

  setting {
    name  = "containerInsights"
    value = "enabled"
  }

  tags = merge(var.tags, {
    Name = "${var.project_name}-cluster"
  })
}

resource "aws_cloudwatch_log_group" "api" {
  for_each = local.envs

  name              = "/aws/ecs/${var.project_name}/api/${each.key}"
  retention_in_days = var.log_retention_days

  tags = merge(var.tags, {
    Name        = "${var.project_name}-api-${each.key}-logs"
    Environment = each.key
  })
}

resource "aws_iam_role" "task_execution" {
  name = "${var.project_name}-ecs-task-execution-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Action = "sts:AssumeRole"
        Effect = "Allow"
        Principal = {
          Service = "ecs-tasks.amazonaws.com"
        }
      }
    ]
  })

  tags = var.tags
}

resource "aws_iam_role_policy_attachment" "task_execution_managed" {
  role       = aws_iam_role.task_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

resource "aws_iam_role_policy" "task_execution_secrets" {
  name = "${var.project_name}-ecs-task-execution-secrets"
  role = aws_iam_role.task_execution.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "ReadAppSecrets"
        Effect   = "Allow"
        Action   = ["secretsmanager:GetSecretValue"]
        Resource = values(var.app_secret_arns)
      },
      {
        Sid      = "KmsDecryptForSecrets"
        Effect   = "Allow"
        Action   = ["kms:Decrypt"]
        Resource = "*"
      }
    ]
  })
}

resource "aws_iam_role" "task" {
  name = "${var.project_name}-ecs-task-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Action = "sts:AssumeRole"
        Effect = "Allow"
        Principal = {
          Service = "ecs-tasks.amazonaws.com"
        }
      }
    ]
  })

  tags = var.tags
}

resource "aws_lb" "api" {
  for_each = local.envs

  name               = "${var.project_name}-${each.key}-alb"
  internal           = false
  load_balancer_type = "application"
  security_groups    = [var.alb_security_group_ids[each.key]]
  subnets            = var.public_subnet_ids

  tags = merge(var.tags, {
    Name        = "${var.project_name}-${each.key}-alb"
    Environment = each.key
  })
}

resource "aws_lb_target_group" "api" {
  for_each = local.envs

  name        = "${var.project_name}-${each.key}-tg"
  port        = var.container_port
  protocol    = "HTTP"
  vpc_id      = var.vpc_id
  target_type = "ip"

  health_check {
    enabled             = true
    path                = "/health/ready"
    matcher             = "200"
    interval            = 30
    timeout             = 5
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }

  tags = merge(var.tags, {
    Name        = "${var.project_name}-${each.key}-tg"
    Environment = each.key
  })
}

resource "aws_lb_listener" "http" {
  for_each = local.envs

  load_balancer_arn = aws_lb.api[each.key].arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type = "redirect"
    redirect {
      port        = "443"
      protocol    = "HTTPS"
      status_code = "HTTP_301"
    }
  }
}

resource "aws_lb_listener" "https" {
  for_each = local.envs

  load_balancer_arn = aws_lb.api[each.key].arn
  port              = 443
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = var.alb_certificate_arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.api[each.key].arn
  }
}

resource "aws_ecs_task_definition" "api" {
  for_each = local.envs

  family                   = "${var.project_name}-api-${each.key}"
  network_mode             = "awsvpc"
  requires_compatibilities = ["FARGATE"]
  cpu                      = each.value.cpu
  memory                   = each.value.memory
  execution_role_arn       = aws_iam_role.task_execution.arn
  task_role_arn            = aws_iam_role.task.arn

  container_definitions = jsonencode([
    {
      name      = "sabr-api"
      image     = "${var.ecr_repository_urls[each.key]}:latest"
      essential = true
      portMappings = [
        {
          containerPort = var.container_port
          hostPort      = var.container_port
          protocol      = "tcp"
        }
      ]
      environment = [
        { name = "ASPNETCORE_URLS", value = "http://+:${var.container_port}" },
        { name = "ASPNETCORE_ENVIRONMENT", value = each.value.app_environment },
        { name = "Cors__AllowedDomain", value = var.root_domain },
        { name = "AllowedOrigins", value = each.value.allowed_origins },
        { name = "MercadoLivre__ClientId", value = "pending-${each.key}-client-id" },
        { name = "MercadoLivre__ClientSecret", value = "pending-${each.key}-client-secret" },
        { name = "MercadoLivre__RedirectUri", value = "https://${each.value.api_subdomain}.${var.root_domain}/integrations/mercadolivre/callback" },
        { name = "MercadoLivre__WebhookSecret", value = "pending-${each.key}-webhook-secret" }
      ]
      secrets = [
        { name = "ConnectionStrings__Default", valueFrom = "${var.app_secret_arns[each.key]}:ConnectionStrings__Default::" },
        { name = "Jwt__Secret", valueFrom = "${var.app_secret_arns[each.key]}:Jwt__Secret::" },
        { name = "Ml__ApiKey", valueFrom = "${var.app_secret_arns[each.key]}:Ml__ApiKey::" }
      ]
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          awslogs-group         = aws_cloudwatch_log_group.api[each.key].name
          awslogs-region        = data.aws_region.current.name
          awslogs-stream-prefix = "ecs"
        }
      }
    }
  ])

  tags = merge(var.tags, {
    Name        = "${var.project_name}-api-${each.key}"
    Environment = each.key
  })
}

data "aws_region" "current" {}

resource "aws_ecs_service" "api" {
  for_each = local.envs

  name            = "${var.project_name}-api-${each.key}"
  cluster         = aws_ecs_cluster.this.id
  task_definition = aws_ecs_task_definition.api[each.key].arn
  desired_count   = each.value.desired_count
  launch_type     = "FARGATE"

  deployment_controller {
    type = "ECS"
  }

  deployment_minimum_healthy_percent = each.key == "dev" ? 50 : 100
  deployment_maximum_percent         = 200

  network_configuration {
    subnets          = var.private_subnet_ids
    security_groups  = [var.ecs_security_group_ids[each.key]]
    assign_public_ip = false
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.api[each.key].arn
    container_name   = "sabr-api"
    container_port   = var.container_port
  }

  depends_on = [aws_lb_listener.https]

  tags = merge(var.tags, {
    Name        = "${var.project_name}-api-${each.key}"
    Environment = each.key
  })
}

resource "aws_appautoscaling_target" "api" {
  for_each = local.envs

  max_capacity       = each.value.max_capacity
  min_capacity       = each.value.min_capacity
  resource_id        = "service/${aws_ecs_cluster.this.name}/${aws_ecs_service.api[each.key].name}"
  scalable_dimension = "ecs:service:DesiredCount"
  service_namespace  = "ecs"
}

resource "aws_appautoscaling_policy" "cpu" {
  for_each = local.envs

  name               = "${var.project_name}-${each.key}-cpu-target"
  policy_type        = "TargetTrackingScaling"
  resource_id        = aws_appautoscaling_target.api[each.key].resource_id
  scalable_dimension = aws_appautoscaling_target.api[each.key].scalable_dimension
  service_namespace  = aws_appautoscaling_target.api[each.key].service_namespace

  target_tracking_scaling_policy_configuration {
    predefined_metric_specification {
      predefined_metric_type = "ECSServiceAverageCPUUtilization"
    }
    target_value       = 65
    scale_in_cooldown  = 120
    scale_out_cooldown = 60
  }
}

resource "aws_appautoscaling_policy" "memory" {
  for_each = local.envs

  name               = "${var.project_name}-${each.key}-memory-target"
  policy_type        = "TargetTrackingScaling"
  resource_id        = aws_appautoscaling_target.api[each.key].resource_id
  scalable_dimension = aws_appautoscaling_target.api[each.key].scalable_dimension
  service_namespace  = aws_appautoscaling_target.api[each.key].service_namespace

  target_tracking_scaling_policy_configuration {
    predefined_metric_specification {
      predefined_metric_type = "ECSServiceAverageMemoryUtilization"
    }
    target_value       = 75
    scale_in_cooldown  = 180
    scale_out_cooldown = 60
  }
}
