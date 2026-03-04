variable "project_name" {
  description = "Project slug used in AWS resource names."
  type        = string
  default     = "sabr"
}

variable "aws_region" {
  description = "Primary AWS region for workloads."
  type        = string
  default     = "sa-east-1"
}

variable "root_domain" {
  description = "Root public domain (example: seudominio.com.br)."
  type        = string
}

variable "route53_zone_id" {
  description = "Existing Route53 Hosted Zone ID. Leave empty to auto-discover by domain name."
  type        = string
  default     = ""
}

variable "vpc_cidr" {
  description = "CIDR for the shared VPC."
  type        = string
  default     = "10.30.0.0/16"
}

variable "public_subnet_cidrs" {
  description = "Two public subnet CIDRs, one per AZ."
  type        = list(string)
  default     = ["10.30.0.0/24", "10.30.1.0/24"]
}

variable "private_subnet_cidrs" {
  description = "Two private subnet CIDRs, one per AZ."
  type        = list(string)
  default     = ["10.30.10.0/24", "10.30.11.0/24"]
}

variable "nat_gateway_mode" {
  description = "NAT topology: single (cost optimized) or per_az (high availability)."
  type        = string
  default     = "single"

  validation {
    condition     = contains(["single", "per_az"], var.nat_gateway_mode)
    error_message = "nat_gateway_mode must be single or per_az."
  }
}

variable "db_username" {
  description = "PostgreSQL admin username for both environments."
  type        = string
  default     = "sabr_user"
}

variable "db_name_prefix" {
  description = "Prefix for RDS database names."
  type        = string
  default     = "sabrdb"
}

variable "db_instance_class_dev" {
  description = "RDS instance class for development."
  type        = string
  default     = "db.t4g.micro"
}

variable "db_instance_class_prod" {
  description = "RDS instance class for production."
  type        = string
  default     = "db.t4g.micro"
}

variable "db_backup_retention_dev" {
  description = "Backup retention (days) for development DB."
  type        = number
  default     = 3
}

variable "db_backup_retention_prod" {
  description = "Backup retention (days) for production DB."
  type        = number
  default     = 14
}

variable "ecs_desired_count_dev" {
  description = "Desired task count for development API service."
  type        = number
  default     = 1
}

variable "ecs_desired_count_prod" {
  description = "Desired task count for production API service."
  type        = number
  default     = 1
}

variable "ecs_min_capacity_dev" {
  description = "Minimum autoscaling task count for development API."
  type        = number
  default     = 1
}

variable "ecs_max_capacity_dev" {
  description = "Maximum autoscaling task count for development API."
  type        = number
  default     = 2
}

variable "ecs_min_capacity_prod" {
  description = "Minimum autoscaling task count for production API."
  type        = number
  default     = 1
}

variable "ecs_max_capacity_prod" {
  description = "Maximum autoscaling task count for production API."
  type        = number
  default     = 4
}

variable "ecs_cpu_dev" {
  description = "CPU units for development API task definition."
  type        = number
  default     = 512
}

variable "ecs_memory_dev" {
  description = "Memory (MiB) for development API task definition."
  type        = number
  default     = 1024
}

variable "ecs_cpu_prod" {
  description = "CPU units for production API task definition."
  type        = number
  default     = 512
}

variable "ecs_memory_prod" {
  description = "Memory (MiB) for production API task definition."
  type        = number
  default     = 1024
}

variable "cloudwatch_log_retention_days" {
  description = "Log retention for ECS application logs."
  type        = number
  default     = 30
}

variable "github_backend_repository" {
  description = "GitHub repository (owner/repo) allowed to deploy backend."
  type        = string
  default     = ""
}

variable "github_frontend_repository" {
  description = "GitHub repository (owner/repo) allowed to deploy frontend."
  type        = string
  default     = ""
}

variable "github_infra_repository" {
  description = "GitHub repository (owner/repo) allowed to run terraform apply."
  type        = string
  default     = ""
}

variable "enable_oidc_roles" {
  description = "Whether to provision GitHub OIDC roles."
  type        = bool
  default     = true
}

variable "alarm_email" {
  description = "Email to receive CloudWatch alarm notifications. Leave empty to disable SNS subscription."
  type        = string
  default     = ""
}

variable "tags" {
  description = "Additional tags applied to all resources."
  type        = map(string)
  default     = {}
}
