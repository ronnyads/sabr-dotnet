locals {
  create_oidc = var.enable_oidc_roles && (
    var.github_backend_repository != "" ||
    var.github_frontend_repository != "" ||
    var.github_infra_repository != ""
  )

  deploy_refs = [
    "ref:refs/heads/develop",
    "ref:refs/heads/main"
  ]

  deploy_environments = [
    "development",
    "production"
  ]
}

resource "aws_iam_openid_connect_provider" "github" {
  count = local.create_oidc ? 1 : 0

  url             = "https://token.actions.githubusercontent.com"
  client_id_list  = ["sts.amazonaws.com"]
  thumbprint_list = ["6938fd4d98bab03faadb97b34396831e3780aea1"]

  tags = local.tags
}

data "aws_iam_policy_document" "backend_assume" {
  count = local.create_oidc && var.github_backend_repository != "" ? 1 : 0

  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]

    principals {
      type        = "Federated"
      identifiers = [aws_iam_openid_connect_provider.github[0].arn]
    }

    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }

    condition {
      test     = "StringLike"
      variable = "token.actions.githubusercontent.com:sub"
      values = concat(
        [for ref in local.deploy_refs : "repo:${var.github_backend_repository}:${ref}"],
        [for env in local.deploy_environments : "repo:${var.github_backend_repository}:environment:${env}"]
      )
    }
  }
}

resource "aws_iam_role" "github_backend_deploy" {
  count = local.create_oidc && var.github_backend_repository != "" ? 1 : 0

  name               = "${var.project_name}-github-backend-deploy"
  assume_role_policy = data.aws_iam_policy_document.backend_assume[0].json
  tags               = local.tags
}

resource "aws_iam_role_policy" "github_backend_deploy" {
  count = local.create_oidc && var.github_backend_repository != "" ? 1 : 0

  name = "${var.project_name}-github-backend-deploy-policy"
  role = aws_iam_role.github_backend_deploy[0].id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "EcrAuth"
        Effect   = "Allow"
        Action   = ["ecr:GetAuthorizationToken"]
        Resource = "*"
      },
      {
        Sid    = "EcrPushPull"
        Effect = "Allow"
        Action = [
          "ecr:BatchCheckLayerAvailability",
          "ecr:CompleteLayerUpload",
          "ecr:GetDownloadUrlForLayer",
          "ecr:BatchGetImage",
          "ecr:InitiateLayerUpload",
          "ecr:PutImage",
          "ecr:UploadLayerPart"
        ]
        Resource = values(module.ecr.repository_arns)
      },
      {
        Sid    = "EcsDeploy"
        Effect = "Allow"
        Action = [
          "ecs:DescribeServices",
          "ecs:DescribeTaskDefinition",
          "ecs:RegisterTaskDefinition",
          "ecs:UpdateService",
          "ecs:ListTaskDefinitions"
        ]
        Resource = "*"
      },
      {
        Sid    = "PassTaskRoles"
        Effect = "Allow"
        Action = ["iam:PassRole"]
        Resource = [
          module.ecs.task_execution_role_arn,
          module.ecs.task_role_arn
        ]
      }
    ]
  })
}

data "aws_iam_policy_document" "frontend_assume" {
  count = local.create_oidc && var.github_frontend_repository != "" ? 1 : 0

  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]

    principals {
      type        = "Federated"
      identifiers = [aws_iam_openid_connect_provider.github[0].arn]
    }

    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }

    condition {
      test     = "StringLike"
      variable = "token.actions.githubusercontent.com:sub"
      values = concat(
        [for ref in local.deploy_refs : "repo:${var.github_frontend_repository}:${ref}"],
        [for env in local.deploy_environments : "repo:${var.github_frontend_repository}:environment:${env}"]
      )
    }
  }
}

resource "aws_iam_role" "github_frontend_deploy" {
  count = local.create_oidc && var.github_frontend_repository != "" ? 1 : 0

  name               = "${var.project_name}-github-frontend-deploy"
  assume_role_policy = data.aws_iam_policy_document.frontend_assume[0].json
  tags               = local.tags
}

resource "aws_iam_role_policy" "github_frontend_deploy" {
  count = local.create_oidc && var.github_frontend_repository != "" ? 1 : 0

  name = "${var.project_name}-github-frontend-deploy-policy"
  role = aws_iam_role.github_frontend_deploy[0].id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "S3List"
        Effect   = "Allow"
        Action   = ["s3:ListBucket"]
        Resource = values(module.s3_cloudfront.bucket_arns)
      },
      {
        Sid      = "S3Objects"
        Effect   = "Allow"
        Action   = ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"]
        Resource = [for arn in values(module.s3_cloudfront.bucket_arns) : "${arn}/*"]
      },
      {
        Sid      = "CloudFrontInvalidation"
        Effect   = "Allow"
        Action   = ["cloudfront:CreateInvalidation", "cloudfront:GetInvalidation"]
        Resource = values(module.s3_cloudfront.distribution_arns)
      }
    ]
  })
}

data "aws_iam_policy_document" "infra_assume" {
  count = local.create_oidc && var.github_infra_repository != "" ? 1 : 0

  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]

    principals {
      type        = "Federated"
      identifiers = [aws_iam_openid_connect_provider.github[0].arn]
    }

    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }

    condition {
      test     = "StringLike"
      variable = "token.actions.githubusercontent.com:sub"
      values = concat(
        [for ref in local.deploy_refs : "repo:${var.github_infra_repository}:${ref}"],
        [for env in local.deploy_environments : "repo:${var.github_infra_repository}:environment:${env}"]
      )
    }
  }
}

resource "aws_iam_role" "github_infra_apply" {
  count = local.create_oidc && var.github_infra_repository != "" ? 1 : 0

  name               = "${var.project_name}-github-infra-apply"
  assume_role_policy = data.aws_iam_policy_document.infra_assume[0].json
  tags               = local.tags
}

resource "aws_iam_role_policy_attachment" "github_infra_apply_admin" {
  count = local.create_oidc && var.github_infra_repository != "" ? 1 : 0

  role       = aws_iam_role.github_infra_apply[0].name
  policy_arn = "arn:aws:iam::aws:policy/AdministratorAccess"
}
