locals {
  envs = ["dev", "prod"]
}

resource "aws_ecr_repository" "api" {
  for_each = toset(local.envs)

  name                 = "${var.project_name}-api-${each.key}"
  image_tag_mutability = "MUTABLE"

  image_scanning_configuration {
    scan_on_push = true
  }

  encryption_configuration {
    encryption_type = "AES256"
  }

  tags = merge(var.tags, {
    Name        = "${var.project_name}-api-${each.key}"
    Environment = each.key
  })
}

resource "aws_ecr_lifecycle_policy" "api" {
  for_each = aws_ecr_repository.api

  repository = each.value.name
  policy = jsonencode({
    rules = [
      {
        rulePriority = 1
        description  = "Keep last 30 images"
        selection = {
          tagStatus   = "any"
          countType   = "imageCountMoreThan"
          countNumber = 30
        }
        action = {
          type = "expire"
        }
      }
    ]
  })
}
