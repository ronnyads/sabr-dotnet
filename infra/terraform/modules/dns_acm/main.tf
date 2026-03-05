data "aws_route53_zone" "selected" {
  count        = var.route53_zone_id == "" ? 1 : 0
  name         = "${var.root_domain}."
  private_zone = false
}

locals {
  hosted_zone_id = var.route53_zone_id != "" ? var.route53_zone_id : data.aws_route53_zone.selected[0].zone_id
  validation_records = {
    for dvo in concat(
      tolist(aws_acm_certificate.alb.domain_validation_options),
      tolist(aws_acm_certificate.cloudfront.domain_validation_options)
    ) : dvo.resource_record_name => {
      name   = dvo.resource_record_name
      record = dvo.resource_record_value
      type   = dvo.resource_record_type
    }...
  }
}

resource "aws_acm_certificate" "alb" {
  domain_name               = "*.${var.root_domain}"
  subject_alternative_names = [var.root_domain]
  validation_method         = "DNS"

  tags = merge(var.tags, {
    Name = "${var.root_domain}-alb-cert"
  })

  lifecycle {
    create_before_destroy = true
  }
}

resource "aws_route53_record" "validation" {
  for_each = local.validation_records

  zone_id = local.hosted_zone_id
  name    = each.value[0].name
  type    = each.value[0].type
  allow_overwrite = true
  ttl     = 60
  records = [each.value[0].record]
}

resource "aws_acm_certificate_validation" "alb" {
  certificate_arn         = aws_acm_certificate.alb.arn
  validation_record_fqdns = distinct([
    for dvo in aws_acm_certificate.alb.domain_validation_options :
    aws_route53_record.validation[dvo.resource_record_name].fqdn
  ])
}

resource "aws_acm_certificate" "cloudfront" {
  provider                  = aws.us_east_1
  domain_name               = "*.${var.root_domain}"
  subject_alternative_names = [var.root_domain]
  validation_method         = "DNS"

  tags = merge(var.tags, {
    Name = "${var.root_domain}-cloudfront-cert"
  })

  lifecycle {
    create_before_destroy = true
  }
}

resource "aws_acm_certificate_validation" "cloudfront" {
  provider                = aws.us_east_1
  certificate_arn         = aws_acm_certificate.cloudfront.arn
  validation_record_fqdns = distinct([
    for dvo in aws_acm_certificate.cloudfront.domain_validation_options :
    aws_route53_record.validation[dvo.resource_record_name].fqdn
  ])
}
