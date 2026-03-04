variable "project_name" {
  type = string
}

variable "private_subnet_ids" {
  type = list(string)
}

variable "rds_security_group_ids" {
  type = map(string)
}

variable "db_username" {
  type = string
}

variable "db_name_prefix" {
  type = string
}

variable "db_instance_class_dev" {
  type = string
}

variable "db_instance_class_prod" {
  type = string
}

variable "db_backup_retention_dev" {
  type = number
}

variable "db_backup_retention_prod" {
  type = number
}

variable "tags" {
  type    = map(string)
  default = {}
}
