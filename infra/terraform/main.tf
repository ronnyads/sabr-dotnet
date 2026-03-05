module "network" {
  source = "./modules/network"

  project_name         = var.project_name
  vpc_cidr             = var.vpc_cidr
  public_subnet_cidrs  = var.public_subnet_cidrs
  private_subnet_cidrs = var.private_subnet_cidrs
  nat_gateway_mode     = var.nat_gateway_mode
  tags                 = local.tags
}

module "security" {
  source = "./modules/security"

  project_name = var.project_name
  vpc_id       = module.network.vpc_id
  tags         = local.tags
}

module "ecr" {
  source = "./modules/ecr"

  project_name = var.project_name
  tags         = local.tags
}

module "rds" {
  source = "./modules/rds"

  project_name             = var.project_name
  root_domain              = var.root_domain
  private_subnet_ids       = module.network.private_subnet_ids
  rds_security_group_ids   = module.security.rds_security_group_ids
  db_username              = var.db_username
  db_name_prefix           = var.db_name_prefix
  db_instance_class_dev    = var.db_instance_class_dev
  db_instance_class_prod   = var.db_instance_class_prod
  db_backup_retention_dev  = var.db_backup_retention_dev
  db_backup_retention_prod = var.db_backup_retention_prod
  tags                     = local.tags
}

module "dns_acm" {
  source = "./modules/dns_acm"

  providers = {
    aws           = aws
    aws.us_east_1 = aws.us_east_1
  }

  root_domain     = var.root_domain
  route53_zone_id = var.route53_zone_id
  tags            = local.tags
}

module "ecs" {
  source = "./modules/ecs"

  project_name           = var.project_name
  root_domain            = var.root_domain
  vpc_id                 = module.network.vpc_id
  public_subnet_ids      = module.network.public_subnet_ids
  private_subnet_ids     = module.network.private_subnet_ids
  alb_security_group_ids = module.security.alb_security_group_ids
  ecs_security_group_ids = module.security.ecs_security_group_ids
  ecr_repository_urls    = module.ecr.repository_urls
  app_secret_arns        = module.rds.app_secret_arns
  alb_certificate_arn    = module.dns_acm.alb_certificate_arn
  desired_count_dev      = var.ecs_desired_count_dev
  desired_count_prod     = var.ecs_desired_count_prod
  min_capacity_dev       = var.ecs_min_capacity_dev
  max_capacity_dev       = var.ecs_max_capacity_dev
  min_capacity_prod      = var.ecs_min_capacity_prod
  max_capacity_prod      = var.ecs_max_capacity_prod
  cpu_dev                = var.ecs_cpu_dev
  memory_dev             = var.ecs_memory_dev
  cpu_prod               = var.ecs_cpu_prod
  memory_prod            = var.ecs_memory_prod
  log_retention_days     = var.cloudwatch_log_retention_days
  tags                   = local.tags
}

module "s3_cloudfront" {
  source = "./modules/s3_cloudfront"

  project_name               = var.project_name
  root_domain                = var.root_domain
  cloudfront_certificate_arn = module.dns_acm.cloudfront_certificate_arn
  tags                       = local.tags
}

resource "aws_route53_record" "api" {
  for_each = {
    dev  = "api-dev"
    prod = "api"
  }

  zone_id = module.dns_acm.hosted_zone_id
  name    = "${each.value}.${var.root_domain}"
  type    = "A"

  alias {
    name                   = module.ecs.alb_dns_names[each.key]
    zone_id                = module.ecs.alb_zone_ids[each.key]
    evaluate_target_health = true
  }
}

resource "aws_route53_record" "frontend" {
  for_each = module.s3_cloudfront.aliases

  zone_id = module.dns_acm.hosted_zone_id
  name    = each.value
  type    = "A"

  alias {
    name                   = module.s3_cloudfront.distribution_domain_names[each.key]
    zone_id                = module.s3_cloudfront.distribution_zone_ids[each.key]
    evaluate_target_health = false
  }
}
