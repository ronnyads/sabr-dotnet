variable "project_name" {
  type = string
}

variable "root_domain" {
  type = string
}

variable "cloudfront_certificate_arn" {
  type = string
}

variable "tags" {
  type    = map(string)
  default = {}
}

variable "create_distributions" {
  description = "Quando false, os S3 buckets e OACs são criados mas as distributions CloudFront não. Usar enquanto a conta AWS não está verificada para CF."
  type        = bool
  default     = true
}
