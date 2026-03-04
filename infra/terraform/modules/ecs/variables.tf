variable "project_name" {
  type = string
}

variable "root_domain" {
  type = string
}

variable "public_subnet_ids" {
  type = list(string)
}

variable "private_subnet_ids" {
  type = list(string)
}

variable "vpc_id" {
  type = string
}

variable "alb_security_group_ids" {
  type = map(string)
}

variable "ecs_security_group_ids" {
  type = map(string)
}

variable "ecr_repository_urls" {
  type = map(string)
}

variable "app_secret_arns" {
  type = map(string)
}

variable "alb_certificate_arn" {
  type = string
}

variable "container_port" {
  type    = number
  default = 8080
}

variable "desired_count_dev" {
  type = number
}

variable "desired_count_prod" {
  type = number
}

variable "min_capacity_dev" {
  type = number
}

variable "max_capacity_dev" {
  type = number
}

variable "min_capacity_prod" {
  type = number
}

variable "max_capacity_prod" {
  type = number
}

variable "cpu_dev" {
  type = number
}

variable "memory_dev" {
  type = number
}

variable "cpu_prod" {
  type = number
}

variable "memory_prod" {
  type = number
}

variable "log_retention_days" {
  type = number
}

variable "tags" {
  type    = map(string)
  default = {}
}
